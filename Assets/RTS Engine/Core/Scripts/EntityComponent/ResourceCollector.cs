﻿using System.Collections.Generic;
using System.Linq;

using UnityEngine;

using RTSEngine.Entities;
using RTSEngine.ResourceExtension;
using RTSEngine.Event;
using RTSEngine.UnitExtension;
using RTSEngine.Model;
using System;
using RTSEngine.Search;

namespace RTSEngine.EntityComponent
{
    public delegate ErrorMessage IsTargetValid<T>(TargetData<T> data) where T : IEntity;

    public class ResourceCollector : FactionEntityTargetProgressComponent<IResource>, IResourceCollector
    {
        public enum ResourceCollectorSearchBehaviour { none = 0, prioritizeLastResourceType = 1 }

        #region Class Attributes
        protected IUnit unit { private set; get; }

        [SerializeField, Tooltip("What types of resources can be collected?")]
        private CollectableResourceData[] collectableResources = new CollectableResourceData[0];
        private IReadOnlyDictionary<ResourceTypeInfo, CollectableResourceData> collectableResourcesDic = null;

        [SerializeField, Tooltip("Define an extra condition for the next target auto-search when the last player assigned target resource reaches maximum collector capacity.")]
        private ResourceCollectorSearchBehaviour onTargetResourceFullSearch = ResourceCollectorSearchBehaviour.prioritizeLastResourceType;
        [SerializeField, Tooltip("Define an extra condition for the next target auto-search when the last player assigned target resource is depleted (reaches 0 health).")]
        private ResourceCollectorSearchBehaviour onTargetResourceDepletedSearch = ResourceCollectorSearchBehaviour.prioritizeLastResourceType;
        private bool isNextAutoSearchConditionActive;
        private IsTargetValid<IResource> nextAutoSearchCondition;
        #endregion

        #region Raising Events
        public event CustomEventHandler<IResourceCollector, SetTargetInputDataEventArgs> OnTargetMaxWorkerReached;
        private void RaiseOnTargetMaxWorkerReached(SetTargetInputData data)
        {
            var handler = OnTargetMaxWorkerReached;
            handler?.Invoke(this, new SetTargetInputDataEventArgs(data));
        }
        #endregion

        #region Initializing/Terminating
        protected sealed override void OnProgressInit()
        {
            unit = Entity as IUnit;

            // Allows for constant access time to collectable resource data rather than having to go through the list each time
            collectableResourcesDic = collectableResources.ToDictionary(cr => cr.type, cr => cr);

            DisableNextAutoSearchCondition();

            if (unit.IsFree)
            {
                logger.LogWarning($"[{GetType().Name} - {Entity.Code}] This component can not be attached to a free unit. The unit must belong to a faction slot!", this);
            }
        }
        #endregion

        #region Handling Events: Collected Resource
        private void HandleTargetHealthUpdated(IEntity resource, HealthUpdateArgs args)
        {
            if (args.Source != unit
                || resource != Target.instance)
                return;

            if (Target.instance.CanAutoCollect && !unit.IsFree)
            {
                resourceMgr.UpdateResource(
                    unit.FactionID,
                    new ResourceInput
                    {
                        type = Target.instance.ResourceType,
                        value = new ResourceTypeValue
                        {
                            amount = -args.Value,
                            capacity = 0
                        }
                    },
                    add: true);
                return;
            }


            unit.DropOffSource.UpdateCollectedResources(Target.instance.ResourceType, -args.Value);

            AttemptDropOff();
        }

        private void HandleTargetResourceDead(IEntity entity, DeadEventArgs e)
        {
            DisableProgress();
            IResource resource = (entity as IResource);

            switch (onTargetResourceDepletedSearch)
            {
                case ResourceCollectorSearchBehaviour.prioritizeLastResourceType:

                    EnableNextAutoSearchCondition((data =>
                        data.instance.ResourceType == resource.ResourceType
                            ? ErrorMessage.none
                            : ErrorMessage.resourceTypeMismatch),
                            new SetTargetInputData { target = resource.ToTargetData() });
                    break;

                default:

                    if (unit.DropOffSource.IsValid() && !resource.CanAutoCollect)
                        unit.DropOffSource.AttemptStartDropOff(force: false, resourceType: resource.ResourceType);
                    break;
            }
        }
        #endregion

        #region Updating Component State
        protected override bool MustStopProgress()
        {
            return (Target.instance.Health.IsDead && (Target.instance.CanAutoCollect || unit.DropOffSource.State == DropOffState.inactive))
                || (!Target.instance.CanCollectOutsideBorder && !RTSHelper.IsSameFaction(Target.instance, factionEntity))
                || (InProgress && !IsTargetInRange(Entity.transform.position, Target) && (Target.instance.CanAutoCollect || unit.DropOffSource.State == DropOffState.inactive))
                || (resourceMgr.HasResourceTypeReachedLimitCapacity(Target.instance.ResourceType, unit.FactionID));
        }

        protected override bool CanEnableProgress()
        {
            return IsTargetInRange(Entity.transform.position, Target)
                && (Target.instance.CanAutoCollect || unit.DropOffSource.State == DropOffState.inactive || unit.DropOffSource.State == DropOffState.goingBack);
        }

        protected override bool CanProgress() => Target.instance.CanAutoCollect || unit.DropOffSource.State == DropOffState.inactive;

        protected override bool MustDisableProgress() => !Target.instance.IsValid() || (!Target.instance.CanAutoCollect && unit.DropOffSource.State != DropOffState.inactive);

        // This defines that if the DropOffSource sent a SetIdle request to the entity, to start its drop off process, the collector component will not be disabled in the process
        public override bool CanStopOnSetIdleSource(IEntityTargetComponent idleSource) => idleSource != unit.DropOffSource;

        protected override bool CanStopOnNoTarget() => InProgress && (Target.instance.CanAutoCollect || unit.DropOffSource.State == DropOffState.inactive);

        protected override void OnProgressStop()
        {
            inProgressObject = null;

            sourceEffect = null;
            targetEffect = null;

            unit.DropOffSource?.Cancel();

            if (LastTarget.instance.IsValid())
            {
                LastTarget.instance.WorkerMgr.Remove(unit);

                LastTarget.instance.Health.EntityHealthUpdated -= HandleTargetHealthUpdated;
                LastTarget.instance.Health.EntityDead -= HandleTargetResourceDead;
            }
        }
        #endregion

        #region Drop Off Handling
        private void AttemptDropOff()
        {
            if (Target.instance.CanAutoCollect)
                return;

            if (unit.DropOffSource.AttemptStartDropOff(force: false, resourceType: Target.instance.ResourceType))
            {
                // Hide the source and target effect objects during drop off.
                ToggleSourceTargetEffect(false);

                DisableProgress();
                return;
            }
            else if (unit.DropOffSource.State != DropOffState.goingBack)
                // Cancel drop off if it was pending
                unit.DropOffSource.Cancel();
        }
        #endregion

        #region Handling Progress
        protected override void OnInProgressEnabled()
        {
            if (collectableResourcesDic[Target.instance.ResourceType].loopProgressAudio)
                audioMgr.PlaySFX(unit.AudioSourceComponent, collectableResourcesDic[Target.instance.ResourceType].progressAudio.Fetch(), true);

            //unit is coming back after dropping off resources?
            if (!Target.instance.CanAutoCollect && unit.DropOffSource.State == DropOffState.goingBack)
                unit.DropOffSource.Cancel();
            else
                globalEvent.RaiseEntityComponentTargetStartGlobal(this, new TargetDataEventArgs(Target));

            unit.MovementComponent.UpdateRotationTarget(Target.instance, Target.instance.transform.position);
        }

        protected override void OnProgress()
        {
            if (!collectableResourcesDic[Target.instance.ResourceType].loopProgressAudio)
                audioMgr.PlaySFX(unit.AudioSourceComponent, collectableResourcesDic[Target.instance.ResourceType].progressAudio.Fetch(), false);

            Target.instance.Health.Add(new HealthUpdateArgs(-collectableResourcesDic[Target.instance.ResourceType].amount, unit));
        }
        #endregion

        #region Searching/Updating Target
        public override bool CanSearch => true;

        public override float GetProgressRange()
            => Target.instance.WorkerMgr.GetOccupiedPosition(unit, out _)
            ? mvtMgr.StoppingDistance
            : base.GetProgressRange();
        public override Vector3 GetProgressCenter()
            => Target.instance.WorkerMgr.GetOccupiedPosition(unit, out Vector3 workerPosition)
            ? workerPosition
            : base.GetProgressCenter();

        public override bool IsTargetInRange(Vector3 sourcePosition, TargetData<IEntity> target)
        {
            if (!target.instance.IsValid() ||
                !(target.instance is IResource))
            {
                return false;
            }

            return (target.instance as IResource).WorkerMgr.GetOccupiedPosition(unit, out Vector3 workerPosition)
                ? mvtMgr.IsPositionReached(sourcePosition, workerPosition)
                : base.IsTargetInRange(sourcePosition, target);
        }

        public bool IsResourceTypeCollectable(ResourceTypeInfo resourceType)
        {
            return collectableResourcesDic != null
                ? collectableResourcesDic.ContainsKey(resourceType)
                : collectableResources.Select(cr => cr.type == resourceType).Any();
        }

        public override bool IsTargetValid(SetTargetInputData data, out ErrorMessage errorMessage)
        {
            errorMessage = IsTargetValid(data);

            return errorMessage == ErrorMessage.none
                || (errorMessage == ErrorMessage.workersMaxAmountReached
                && onTargetResourceFullSearch == ResourceCollectorSearchBehaviour.prioritizeLastResourceType);
        }

        public override ErrorMessage IsTargetValid (SetTargetInputData data)
        {
            TargetData<IResource> potentialTarget = data.target;

            if (!potentialTarget.instance.IsValid())
                return ErrorMessage.invalid;
            if (!factionEntity.IsInteractable)
                return ErrorMessage.invalid;
            if (!potentialTarget.instance.IsInteractable)
                return ErrorMessage.uninteractable;
            if (!potentialTarget.instance.CanCollect)
                return ErrorMessage.resourceNotCollectable;
            if (!IsResourceTypeCollectable(potentialTarget.instance.ResourceType))
                return ErrorMessage.targetPickerUndefined;
            if (potentialTarget.instance.Health.IsDead)
                return ErrorMessage.targetDead;
            if (!factionEntity.CanMove() && !IsTargetInRange(Entity.transform.position, potentialTarget))
                return ErrorMessage.targetOutOfRange;
            if (!potentialTarget.instance.CanCollectOutsideBorder && !potentialTarget.instance.IsFriendlyFaction(factionEntity))
                return ErrorMessage.resourceOutsideTerritory;
            if (resourceMgr.HasResourceTypeReachedLimitCapacity(potentialTarget.instance.ResourceType, unit.FactionID))
                return ErrorMessage.resourceTypeLimitCapacityReached;
            if (!potentialTarget.instance.CanAutoCollect)
            {
                if (!unit.DropOffSource.IsValid())
                    return ErrorMessage.dropOffComponentMissing;
                if (!isNextAutoSearchConditionActive && unit.DropOffSource.HasReachedMaxCapacity(potentialTarget.instance.ResourceType))
                {
                    unit.DropOffSource.AttemptStartDropOff(force: true, potentialTarget.instance.ResourceType);
                    return ErrorMessage.dropOffMaxCapacityReached;
                }
            }
            if (OnNextAutoSearchCondition(potentialTarget, out ErrorMessage errorMessage))
                return errorMessage;

            ErrorMessage workerMgrError = potentialTarget.instance.WorkerMgr.CanMove(
                unit,
                new AddableUnitData
                {
                    allowDifferentFaction = true
                });

            if (workerMgrError == ErrorMessage.workersMaxAmountReached)
                RaiseOnTargetMaxWorkerReached(data);

            return workerMgrError;
        }

        protected override void OnTargetPostLocked(SetTargetInputData input, bool sameTarget)
        {
            if (!logger.RequireTrue(Target.instance.CanAutoCollect || unit.DropOffSource.IsValid(),
                $"[{GetType().Name} - {Entity.Code}] A component that extends {typeof(IDropOffSource).Name} interface must be attached to this resource collector since resources can not be auto collected!"))
                return;

            var handler = collectableResourcesDic[Target.instance.ResourceType];
            // In this component, the in progress object depends on the type of resource that is being collected.
            inProgressObject = handler.enableObject;
            progressOverrideController = handler.animatorOverrideController;
            progressEnabledAudio = handler.enableAudio;
            sourceEffect = handler.sourceEffect;
            targetEffect = handler.targetEffect;

            if (Target.instance.WorkerMgr.Move(
                unit,
                new AddableUnitData(
                    sourceTargetComponent: this,
                    input,
                    allowDifferentFaction: Target.instance.CanCollectOutsideBorder)) != ErrorMessage.none)
            {
                unit.DropOffSource?.Cancel();
                Stop();
                return;
            }

            // If the movement component is unable to calculate the path towards the target, it will set the unit back to idle
            // And in this case, we do not continue
            if (!unit.MovementComponent.HasTarget)
            {
                Stop();
                return;
            }

            if(input.playerCommand && unit.IsLocalPlayerFaction())
                audioMgr.PlaySFX(handler.orderAudio, unit, loop: false);

            // For the worker component manager, make sure that enough worker positions is available even in the local method.
            // Since they are only updated in the local method, meaning that the SetTarget method would always relay the input in case a lot of consecuive calls are made...
            //... on the same resource from multiple collectors.

            if (sameTarget)
            {
                AttemptDropOff();
                return;
            }

            if (!Target.instance.Health.IsValid())
                logger.LogError($"[ResourceCollector] Target resource of code '{Target.instance.Code}' does not have a valid health component.", source: Target.instance);

            Target.instance.Health.EntityHealthUpdated += HandleTargetHealthUpdated;
            Target.instance.Health.EntityDead += HandleTargetResourceDead;

            globalEvent.RaiseEntityComponentTargetLockedGlobal(this, new TargetDataEventArgs(Target));

            AttemptDropOff();
        }
        #endregion

        #region Handling Next Auto-Search Behaviour
        private void DisableNextAutoSearchCondition()
        {
            isNextAutoSearchConditionActive = false;
            nextAutoSearchCondition = null;
        }

        private bool EnableNextAutoSearchCondition(IsTargetValid<IResource> condition, SetTargetInputData lastInput)
        {
            if (!condition.IsValid())
                return false;

            nextAutoSearchCondition = condition;
            isNextAutoSearchConditionActive = true;

            if(gridSearch.Search(lastInput.target.instance.transform.position, TargetFinderData.range, IsTargetValid, playerCommand: false, out IResource potentialTarget) == ErrorMessage.none)
            {
                SetTarget(new SetTargetInputData
                {
                    target = new TargetData<IEntity>
                    {
                        instance = potentialTarget,
                        position = potentialTarget.transform.position
                    },
                    playerCommand = lastInput.playerCommand,
                    fromRallypoint = lastInput.fromRallypoint,
                    fromTasksQueue = lastInput.fromTasksQueue,
                });
            }

            DisableNextAutoSearchCondition();

            return true;
        }

        private bool OnNextAutoSearchCondition(TargetData<IResource> data, out ErrorMessage errorMsg)
        {
            errorMsg = ErrorMessage.none;
            if (!isNextAutoSearchConditionActive)
                return false;

            errorMsg = nextAutoSearchCondition(data);
            return errorMsg != ErrorMessage.none;
        }

        protected override void OnSetTargetError(SetTargetInputData input, ErrorMessage errorMsg)
        {
            if (!input.playerCommand && !input.fromRallypoint)
                return;

            switch (errorMsg)
            {
                case ErrorMessage.workersMaxAmountReached:
                    switch(onTargetResourceFullSearch)
                    {
                        case ResourceCollectorSearchBehaviour.prioritizeLastResourceType:
                            IResource lastResource = (input.target.instance as IResource);

                            EnableNextAutoSearchCondition((data =>
                                data.instance.ResourceType == lastResource.ResourceType
                                    ? ErrorMessage.none
                                    : ErrorMessage.resourceTypeMismatch),
                                    input);

                            break;
                    }
                    break;
            }
        }
        #endregion
    }
}
