﻿using System;

using UnityEngine;

using RTSEngine.EntityComponent;
using RTSEngine.BuildingExtension;
using RTSEngine.Event;
using RTSEngine.Game;
using RTSEngine.Health;
using RTSEngine.Selection;

namespace RTSEngine.Entities
{
    public class Building : FactionEntity, IBuilding
    {
        #region Class Attributes
        public override EntityType Type => EntityType.building;

        private BuildingPlacedData placedData;

        public bool IsPlacementInstance { private set; get; } = false;

        public override bool IsDummy => IsPlacementInstance;

        public bool IsBuilt { private set; get; }
        private bool isPlaced;
        public sealed override bool CanLaunchTask => base.CanLaunchTask && IsBuilt;

        // value is overriden by the init parameters
        private bool canGiveInitResources;

        public IBorder CurrentCenter { private set; get; }

        public IBorder BorderComponent { private set; get; }
        public IBuildingPlacer PlacerComponent { private set; get; }

        public new IBuildingHealth Health { private set; get; }
        public new IBuildingWorkerManager WorkerMgr { private set; get; }
        #endregion

        #region Raising Events
        public event CustomEventHandler<IBuilding, EventArgs> BuildingBuilt;
        private void RaiseBuildingBuilt()
        {
            var handler = BuildingBuilt;
            handler?.Invoke(this, EventArgs.Empty);
        }
        #endregion

        #region Initializing/Terminating
        public void Init(IGameManager gameMgr, InitBuildingParameters initParams)
        {
            this.placedData = initParams.placedData;
            IsBuilt = false;
            isPlaced = false;
            canGiveInitResources = initParams.giveInitResources;

            base.Init(gameMgr, initParams);

            //the building center is set to itself if it includes an IBorderComponent
            this.CurrentCenter = BorderComponent.IsValid() ? BorderComponent : initParams.buildingCenter;

            if (!IsPlacementInstance)
                Place(completeConstruction: initParams.isBuilt, initParams.playerCommand);
        }

        protected override void FetchComponents()
        {
            BorderComponent = transform.GetComponentInChildren<IBorder>();

            PlacerComponent = transform.GetComponentInChildren<IBuildingPlacer>();
            if (!logger.RequireValid(PlacerComponent,
                $"[{GetType().Name} - {Code}] Building object must have a component that extends {typeof(IBuildingPlacer).Name} interface attached to it!"))
                return;

            Health = transform.GetComponentInChildren<IBuildingHealth>();

            WorkerMgr = transform.GetComponentInChildren<IBuildingWorkerManager>();

            base.FetchComponents();

            // IEntity gets the WorkerMgr component
            if (!logger.RequireValid(WorkerMgr,
                $"[{GetType().Name} - {Code}] Building object must have a component that extends {typeof(IEntityWorkerManager).Name} interface attached to it!"))
                return;

        }

        protected override void SubToEvents()
        {
            base.SubToEvents();

            //subscribe to events
            Health.EntityHealthUpdated += HandleBuildingHealthUpdated;
        }

        public void InitPlacementInstance (IGameManager gameMgr, InitBuildingParameters initParams)
        {
            IsPlacementInstance = true;

            Init(gameMgr, initParams);

            CurrentCenter = initParams.buildingCenter;

            if(this.IsLocalPlayerFaction() && SelectionMarker.IsValid())
                SelectionMarker.Enable(Color.green);
        }

        protected sealed override void Disable(bool isUpgrade, bool isFactionUpdate)
        {
            base.Disable(isUpgrade, isFactionUpdate);

            if (!IsFree)
            {
                if (BorderComponent.IsValid()) 
                    BorderComponent.Disable();
            }

            if(!isFactionUpdate)
                Health.EntityHealthUpdated -= HandleBuildingHealthUpdated; //just in case the building is destroyed before it is fully constructed.

            OnDisabled(isUpgrade, isFactionUpdate);
        }

        protected virtual void OnDisabled(bool isUpgrade, bool isFactionUpdate) { }
        #endregion

        #region Handling Events: Building Health
        private void HandleBuildingHealthUpdated(IEntity sender, HealthUpdateArgs e)
        {
            if(Health.HasMaxHealth)
                CompleteConstruction();
        }
        #endregion

        #region Updating Building State: Placed, ConstructionComplete
        private void Place(bool completeConstruction, bool playerCommand)
        {
            if (isPlaced)
                return;

            // Hide the selection marker since it was used to display whether the building can be placed or not.
            SelectionMarker?.Disable(); 

            CompleteInit();
            PlacerComponent.InitPlaced(placedData);

            globalEvent.RaiseBuildingPlacedGlobal(this);

            isPlaced = true;

            if (completeConstruction)
                CompleteConstruction();

            if (IsFree) //free builidng? job is done here
                return;

            if (!RTSHelper.IsLocalPlayerFaction(this))
                return;

            // Since the *SetTarget* sends an input command and would not return whether the target would be set or not.
            // We determine the amount of missing workers and see what builder units from the ones selected can fulfill these positions
            // And if the missing workers amount can be filled from the selected units, we stop looking to make it more efficient
            int missingWorkers = WorkerMgr.MaxAmount - WorkerMgr.Amount;
            foreach (IUnit unit in selectionMgr.GetEntitiesList(EntityType.unit, exclusiveType: false, localPlayerFaction: true))
            {
                if (missingWorkers <= 0)
                    break;
                if (unit.BuilderComponent.IsValid()
                     && unit.BuilderComponent.IsTargetValid(this.ToSetTargetInputData(playerCommand :false)) == ErrorMessage.none)
                {
                    if(unit.TasksQueue.IsValid() && taskMgr.IsTaskQueueEnabled)
                    {
                        unit.TasksQueue.Add(new SetTargetInputData 
                        {
                            componentCode = unit.BuilderComponent.Code,

                            target = this,
                            playerCommand = playerCommand,
                        });
                    }
                    else
                        unit.BuilderComponent.SetTarget(this, playerCommand);

                    missingWorkers--;
                }
            }
        }

        private void CompleteConstruction()
        {
            Health.EntityHealthUpdated -= HandleBuildingHealthUpdated;

            if (IsBuilt)
                return;

            // If this is not a placement instance but it has not been placed (initialized fully)
            // Place it before it gets completely constructed.
            if (!IsPlacementInstance && !IsInitialized)
            {
                Place(completeConstruction: true, playerCommand: false);
                // return here because Place() will call CompleteConsutrction() after setting IsInitialized to true
                return;
            }

            bool wasSelectedOnly = selectionMgr.IsSelectedOnly(this);
            if (wasSelectedOnly)
                selectionMgr.Remove(this);

            IsBuilt = true;

            // To reload the single selection UI handlers post construction that listen to selection events
            if (wasSelectedOnly)
                selectionMgr.Add(this, SelectionType.single, isLocalPlayerClickSelection: false);

            if (IsFree)
            {
                TriggerConstructionCompleteEvents();
                return;
            }

            if (BorderComponent.IsValid())
            {
                BorderComponent.Init(gameMgr, this);
                CurrentCenter = BorderComponent;
            }

            if(canGiveInitResources)
                resourceMgr.UpdateResource(FactionID, InitResources, add:true);

            TriggerConstructionCompleteEvents();
        }

        private void TriggerConstructionCompleteEvents()
        {
            RaiseBuildingBuilt();
            globalEvent.RaiseBuildingBuiltGlobal(this);
            OnConstructionComplete();
        }

        protected virtual void OnConstructionComplete() { }
        #endregion
        protected override void OnFactionUpdated () 
        {
            if (!BorderComponent.IsValid())
                return;

            BorderComponent.Disable();

            if (!IsFree)
            {
                BorderComponent.Init(gameMgr, this);
                CurrentCenter = BorderComponent;
            }
        }

    }
}
