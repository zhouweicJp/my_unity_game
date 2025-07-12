﻿using System.Collections.Generic;
using System.Linq;

using UnityEngine;

using RTSEngine.EntityComponent;
using RTSEngine.Selection;
using RTSEngine.Health;
using RTSEngine.Event;
using RTSEngine.Animation;
using RTSEngine.Upgrades;
using RTSEngine.Task;
using RTSEngine.Game;
using RTSEngine.Logging;
using RTSEngine.Faction;
using RTSEngine.Determinism;
using RTSEngine.UnitExtension;
using RTSEngine.Utilities;
using RTSEngine.Attack;
using RTSEngine.Movement;
using RTSEngine.Minimap.Icons;

namespace RTSEngine.Entities
{
    public abstract class Entity : MonoBehaviour, IEntity
    {
        #region Class Attributes
        [HideInInspector]
        public Int2D tabID = new Int2D { x = 0, y = 0 };

        public abstract EntityType Type { get; }

        public bool IsInitialized { private set; get; }

        //multiplayer related:
        public int Key { private set; get; }

        [SerializeField, Tooltip("Name of the entity that will be displayed in UI elements.")]
        private string _name = "entity_name";
        public string Name => _name;

        [SerializeField, EntityCodeInput(isDefiner: true), Tooltip("Unique code for each entity to be used to identify the entity type in the RTS Engine.")]
        private string code = "entity_code";
        public string Code => code;

        [SerializeField, EntityCategoryInput(isDefiner: true), Tooltip("A category that is used to define a group of entities. You can input multiple categories separated by a ','.")]
        private string category = "entity_category";
        public IEnumerable<string> Category => category.Split(',');

        [SerializeField, TextArea(minLines: 5, maxLines: 5), Tooltip("Description of the entity to be displayed in UI elements.")]
        private string description = "entity_description";
        public string Description => description;

        [SerializeField, IconDrawer, Tooltip("Icon of the entity to be displayed in UI elements.")]
        private Sprite icon = null;
        public Sprite Icon => icon;

        [SerializeField, Tooltip("Defines the range that the entity is supposed to occupy on the map. This is represented by the blue sphere gizmo.")]
        private float radius = 2.0f;
        public float Radius { get { return radius; } protected set { radius = value; } }
        /// <summary>
        /// Updates the 'Radius' property of the entity. Make sure this call is done locally on all client instances in case of a multiplayer game to avoid desync!
        /// </summary>
        /// <param name="newRadius">New radius value.</param>
        public void UpdateRadius(float newRadius)
        {
            this.Radius = newRadius;
        }

        [SerializeField, Tooltip("Drag and drop the model object of the entity into this field. Make sure the model object has the 'EntityModelConnections' component attached to it.")]
        private GameObject model = null;
        public GameObject Model => model;

        public bool IsFree { protected set; get; }
        public int FactionID { protected set; get; }
        public IFactionSlot Slot => gameMgr.GetFactionSlot(FactionID);

        public Color SelectionColor { protected set; get; }

        public AudioSource AudioSourceComponent { private set; get; }

        public IAnimatorController AnimatorController { private set; get; }
        public IEntitySelection Selection { private set; get; }
        public IEntitySelectionMarker SelectionMarker { private set; get; }
        public IEntityHealth Health { protected set; get; }
        public IEntityWorkerManager WorkerMgr { private set; get; }

        public virtual bool CanLaunchTask => IsInitialized && !Health.IsDead;

        private bool interactable;

        public virtual bool IsDummy => false;

        public bool IsInteractable {
            protected set => interactable = value;
            get => gameObject.activeInHierarchy && interactable && IsInitialized;
        }
        public bool IsSearchable => IsInteractable;

        [SerializeField, ReadOnly]
        private bool isIdle;
        public bool IsIdle => isIdle;

        // Minimap Icon
        public IEntityMinimapIconHandler MinimapIconHandler { private set; get; }

        //entity components:
        public IReadOnlyDictionary<string, IEntityComponent> EntityComponents { private set; get; }

        public IReadOnlyList<IPendingTaskEntityComponent> PendingTaskEntityComponents { private set; get; }

        public IPendingTasksHandler PendingTasksHandler { private set; get; }
        public IEntityTasksQueueHandler TasksQueue { private set; get; }

        public IReadOnlyDictionary<string, IAddableUnit> AddableUnitComponents { private set; get; }

        public IMovementComponent MovementComponent { private set; get; }

        public bool CanMove() => MovementComponent.IsValid() && MovementComponent.IsActive;
        public virtual bool CanMove(bool playerCommand) => CanMove();

        private IEntityTargetComponent[] entityTargetComponents;
        private HashSet<IEntityTargetComponent> idleEntityTargetComponents;
        public IReadOnlyDictionary<string, IEntityTargetComponent> EntityTargetComponents { private set; get; }
        public IReadOnlyDictionary<string, IEntityTargetProgressComponent> EntityTargetProgressComponents { private set; get; }

        private IAttackComponent[] attackComponents;
        public IReadOnlyList<IAttackComponent> AttackComponents => attackComponents;
        public IReadOnlyDictionary<string, IAttackComponent> AttackComponentsDic { private set; get; }
        public IAttackComponent FirstActiveAttackComponent => attackComponents.Where(comp => comp.IsActive).FirstOrDefault();
        public IEnumerable<IAttackComponent> ActiveAttackComponents => attackComponents.Where(comp => comp.IsActive);
        public bool CanAttack => FirstActiveAttackComponent.IsValid() && FirstActiveAttackComponent.IsActive;

        // Services
        protected IGameManager gameMgr { private set; get; }
        protected IGameLoggingService logger { private set; get; }
        protected IGlobalEventPublisher globalEvent { private set; get; }
        protected ISelector selector { private set; get; }
        protected ISelectionManager selectionMgr { private set; get; } 
        protected IInputManager inputMgr { private set; get; }
        protected IEntityComponentUpgradeManager entityComponentUpgradeMgr { private set; get; } 
        protected ITaskManager taskMgr { private set; get; }
        protected IAttackManager attackMgr { private set; get; }
        protected IMovementManager mvtMgr { private set; get; }
        protected IPlayerMessageHandler playerMsgHandler { private set; get; } 
        #endregion

        #region Raising Events
        public event CustomEventHandler<IEntity, System.EventArgs> EntityInitiated;
        private void RaiseEntityInitiated()
        {
            var handler = EntityInitiated;
            handler?.Invoke(this, System.EventArgs.Empty);
        }

        public event CustomEventHandler<IEntity, System.EventArgs> EntityEnterIdle;
        private void RaiseEntityEnterIdle()
        {
            isIdle = true;
            var handler = EntityEnterIdle;
            handler?.Invoke(this, System.EventArgs.Empty);
        }
        public event CustomEventHandler<IEntity, System.EventArgs> EntityExitIdle;
        private void RaiseEntityExitIdle()
        {
            isIdle = false;
            var handler = EntityExitIdle;
            handler?.Invoke(this, System.EventArgs.Empty);
        }

        public event CustomEventHandler<IEntity, FactionUpdateArgs> FactionUpdateStart;
        public event CustomEventHandler<IEntity, FactionUpdateArgs> FactionUpdateComplete;
        protected void RaiseFactionUpdateStart(FactionUpdateArgs eventArgs)
        {
            var handler = FactionUpdateStart;
            handler?.Invoke(this, eventArgs);
        }
        protected void RaiseFactionUpdateComplete(FactionUpdateArgs eventArgs)
        {
            var handler = FactionUpdateComplete;
            handler?.Invoke(this, eventArgs);
        }

        public event CustomEventHandler<IEntity, EntityComponentUpgradeEventArgs> EntityComponentUpgraded;
        private void RaiseEntityComponentUpgraded(EntityComponentUpgradeEventArgs args)
        {
            var handler = EntityComponentUpgraded;
            handler?.Invoke(this, args);
        }
        #endregion

        #region Initializing/Terminating
        public virtual void InitPrefab(IGameManager gameMgr)
        {
            if (!gameMgr.GetService<IGameLoggingService>().RequireValid(model,
                $"[{GetType().Name} - {Code}] The 'Model' field must be assigned!", source: this))
                return;

            // Immediately set parent to null since some model cache aware calculations require the entity to be parentless
            transform.SetParent(null, worldPositionStays: true);

            foreach (var component in GetComponents<IEntityPrefabInitializable>())
                component.OnPrefabInit(this, gameMgr);
        }

        public virtual void Init(IGameManager gameMgr, InitEntityParameters initParams)
        {
            this.gameMgr = gameMgr;
            this.logger = this.gameMgr.GetService<IGameLoggingService>();

            if(!logger.RequireTrue(!IsInitialized,
                $"[{GetType().Name} - {Code}] Entity has been already initiated!"))
                return;

            if (!initParams.free && !initParams.factionID.IsValidFaction())
            {
                logger.LogError($"[{GetType().Name} - {Code}] Initializing entity with invalid faction ID '{initParams.factionID}'!");
                return;
            }

            this.inputMgr = gameMgr.GetService<IInputManager>();

            // Immediately set parent to null since some model cache aware calculations require the entity to be parentless
            transform.SetParent(null, worldPositionStays: true);

            this.IsFree = initParams.free;
            this.FactionID = IsFree ? RTSHelper.FREE_FACTION_ID : initParams.factionID;

            this.globalEvent = this.gameMgr.GetService<IGlobalEventPublisher>();
            this.selector = this.gameMgr.GetService<ISelector>();
            this.selectionMgr = gameMgr.GetService<ISelectionManager>(); 
            this.entityComponentUpgradeMgr = gameMgr.GetService<IEntityComponentUpgradeManager>();
            this.taskMgr = gameMgr.GetService<ITaskManager>();
            this.attackMgr = gameMgr.GetService<IAttackManager>();
            this.mvtMgr = gameMgr.GetService<IMovementManager>();
            this.playerMsgHandler = gameMgr.GetService<IPlayerMessageHandler>(); 

            //get the components attached to the entity
            HandleComponentUpgrades();
            FetchEntityComponents();
            FetchComponents();

            // Dummy entities are entities that would not be registered for the faction but are used to fulfil a local service like placement buildings
            if (!IsDummy)
            {
                Key = inputMgr.RegisterEntity(this, initParams);
            }

            SubToEvents();

            //entity parent objects are set to ignore raycasts because selection relies on raycasting selection objects which are typically direct children of the entity objects.
            gameObject.layer = LayerMask.NameToLayer("Ignore Raycast");

            InitPriorityComponents();

            UpdateSelectionColor();

            InitComponents(initPre: true);

            if(initParams.setInitialHealth)
                //must bypass the "CanAdd" conditions in IEntityHealth since the initial health value is enforced.
                //This is also called for all clients in a multiplayer game.
                Health.AddLocal(new HealthUpdateArgs(initParams.initialHealth - Health.CurrHealth, null));
        }

        protected void CompleteInit()
        {
            //by default, an entity is interactable.
            IsInteractable = true;
            IsInitialized = true;

            InitComponents(initPost: true);

            RaiseEntityInitiated();
            globalEvent.RaiseEntityInitiatedGlobal(this);

            OnInitComplete();
        }

        protected virtual void OnInitComplete() { }

        protected void InitPriorityComponents()
        {
            foreach (IEntityPriorityPreInitializable component in transform
                .GetComponentsInChildren<IEntityPriorityPreInitializable>()
                .OrderBy(component => component.PreInitPriority))
                component.OnEntityPreInit(gameMgr, this);
        }

        protected void InitComponents(bool initPre = false, bool initPost = false)
        {
            if (initPre)
                foreach (IEntityPreInitializable component in transform.GetComponentsInChildren<IEntityPreInitializable>())
                    component.OnEntityPreInit(gameMgr, this);

            if (initPost)
            {
                foreach (IEntityPostInitializable component in transform.GetComponentsInChildren<IEntityPostInitializable>())
                    component.OnEntityPostInit(gameMgr, this);
            }
        }

        protected void DisableComponents()
        {
            foreach (IEntityPreInitializable component in transform.GetComponentsInChildren<IEntityPreInitializable>())
                component.Disable();

            if (!IsInitialized)
                return;

            foreach (IEntityPostInitializable component in transform.GetComponentsInChildren<IEntityPostInitializable>())
                component.Disable();
        }

        private void HandleComponentUpgrades()
        {
            if (IsFree 
                || !entityComponentUpgradeMgr.TryGet(this, FactionID, out List<UpgradeElement<IEntityComponent>> componentUpgrades))
                return;

            foreach(UpgradeElement<IEntityComponent> element in componentUpgrades)
                UpgradeComponent(element);
        }

        //the assumption here is that the targetComponent is attached to an empty prefab game object that includes no additional components!
        //initTime is set to true when this method is called from the initializer method of the Entity, in that case, no need to init the new component/re-fetch components
        //if this method is called from outside the Init() method of this class, then the initTime must be set to false so that components can be refetched and the new component is initialized
        public void UpgradeComponent(UpgradeElement<IEntityComponent> upgradeElement)
        {
            //get the component to be upgraded, destroy it and replace it with the target upgrade component
            //both components must be valid
            if (!upgradeElement.target.IsValid())
                return;

            RTSHelper.TryGetEntityComponentWithCode(this, upgradeElement.sourceCode, out IEntityComponent sourceComponent);

            //since components with their field values can not be added directly, we create a child object of the entity with the upgraded component
            Transform newComponentTransform = Instantiate(upgradeElement.target.gameObject).transform;
            newComponentTransform.SetParent(transform, true);
            newComponentTransform.transform.localPosition = Vector3.zero;
            IEntityComponent newEntityComponent = newComponentTransform.GetComponent<IEntityComponent>();

            if (AnimatorController.IsValid())
                AnimatorController.LockState = true;

            RaiseEntityComponentUpgraded(new EntityComponentUpgradeEventArgs(sourceComponent, newEntityComponent));

            UnsubFromEvents();

            if (IsInitialized)
            {
                // Disable the old entity component flagging it to make it unfetchable // this can happen in the upgrade method itself
                // Fetch mvt, attack and special entity components through the fetched entity components that do not include disabled ones
                FetchEntityComponents(sourceComponent);
                FetchComponents();

                SubToEvents();

                newEntityComponent.OnEntityPostInit(gameMgr, this);
                if (sourceComponent.IsValid())
                    newEntityComponent.HandleComponentUpgrade(sourceComponent);
            }

            //disable old component
            if (sourceComponent.IsValid())
            {
                if(IsInitialized) // Only disable the component if the entity has been initialized
                    sourceComponent.Disable();

                DestroyImmediate(sourceComponent as UnityEngine.Object);
            }

            if (AnimatorController.IsValid())
                AnimatorController.LockState = false;
        }

        protected virtual void SubToEvents()
        {
            //subscribe to events:
            Health.EntityDead += HandleEntityDead;
            Selection.Selected += HandleEntitySelected;

            for (int i = 0; i < entityTargetComponents.Length; i++)
            {
                entityTargetComponents[i].TargetUpdated += HandleEntityTargetComponentTargetUpdated;
                entityTargetComponents[i].TargetStop += HandleEntityTargetComponentTargetStop;

                if (entityTargetComponents[i].IsIdle)
                {
                    idleEntityTargetComponents.Add(entityTargetComponents[i]);
                }
            }

            if (idleEntityTargetComponents.Count == entityTargetComponents.Length)
            {
                if (!isIdle)
                {
                    RaiseEntityEnterIdle();
                }
            }
            else if (isIdle)
                RaiseEntityExitIdle();
        }

        protected virtual void UnsubFromEvents()
        {
            if (!IsInitialized)
                return;

            Health.EntityDead -= HandleEntityDead;
            Selection.Selected -= HandleEntitySelected;

            for (int i = 0; i < entityTargetComponents.Length; i++)
            {
                entityTargetComponents[i].TargetUpdated -= HandleEntityTargetComponentTargetUpdated;
                entityTargetComponents[i].TargetStop -= HandleEntityTargetComponentTargetStop;
            }

            idleEntityTargetComponents.Clear();

            if (Health.IsDead)
                return;

            if (!isIdle)
            {
                RaiseEntityEnterIdle();
            }
        }

        private void FetchEntityComponents(IEntityComponent exception)
            => FetchEntityComponents(Enumerable.Repeat(exception, 1));
        private void FetchEntityComponents ()
        {
            //finding and initializing entity components.
            IEntityComponent[] entityComponents = transform
                .GetComponentsInChildren<IEntityComponent>();

            FetchEntityComponentsInternal(entityComponents);
        }
        private void FetchEntityComponents (IEnumerable<IEntityComponent> exceptions)
        {
            //finding and initializing entity components.
            IEntityComponent[] entityComponents = transform
                .GetComponentsInChildren<IEntityComponent>()
                .Except(exceptions)
                .ToArray();

            FetchEntityComponentsInternal(entityComponents);
        }
        private void FetchEntityComponentsInternal(IEntityComponent[] entityComponents)
        {
            if (!logger.RequireTrue(entityComponents.Select(comp => comp.Code).Distinct().Count() == entityComponents.Length,
                $"[{GetType().Name} - {Code}] All entity components attached to the entity must each have a distinct code to identify it within the entity!"))
                return;

            EntityComponents = entityComponents.ToDictionary(comp => comp.Code);

            PendingTaskEntityComponents = entityComponents
                .Where(comp => comp is IPendingTaskEntityComponent)
                .Cast<IPendingTaskEntityComponent>()
                .ToList();
        }

        protected T GetEntityComponent<T>() where T : IEntityComponent
        {
            return (T)EntityComponents.Values.FirstOrDefault(comp => comp is T);
        }    
        protected IEnumerable<T> GetEntityComponents<T>() where T : IEntityComponent
        {
            return EntityComponents.Values.Where(comp => comp is T).Cast<T>();
        }    

        protected virtual void FetchComponents()
        {
            AnimatorController = transform.GetComponentInChildren<IAnimatorController>();

            Selection = transform.GetComponentInChildren<IEntitySelection>();
            if (!logger.RequireValid(Selection,
                $"[{GetType().Name} - {Code}] A selection component that extends {typeof(IEntitySelection).Name} must be assigned to the 'Selection' field!"))
                return;

            SelectionMarker = GetComponentInChildren<IEntitySelectionMarker>();

            // The Health component is assigned in the childrten of this class before this is called.
            if (!logger.RequireValid(Health,
                $"[{GetType().Name} - {Code}] An entity health component that extends {typeof(IEntityHealth).Name} must be assigned attache to the entity!"))
                return;

            WorkerMgr = transform.GetComponentInChildren<IEntityWorkerManager>();

            //get the audio source component attached to the entity main object:
            AudioSourceComponent = transform.GetComponentInChildren<AudioSource>();

            PendingTasksHandler = transform.GetComponentInChildren<IPendingTasksHandler>();
            TasksQueue = transform.GetComponentInChildren<IEntityTasksQueueHandler>();

            AddableUnitComponents = transform.GetComponentsInChildren<IAddableUnit>().ToDictionary(comp => comp.Code);

            // Entity Components: Fetch from EntityComponents instead of GetComponentInChildren!!
            MovementComponent = GetEntityComponent<IMovementComponent>();

            if (!logger.RequireTrue(GetEntityComponents<IMovementComponent>().Count() < 2,
                $"[{GetType().Name} - {Code}] Having more than one components that extend {typeof(IMovementComponent).Name} interface attached to the same entity is not allowed!"))
                return;

            // If the entity was already initialized then unsub from the entity target components events
            // In case this is a reload for entity components due to an upgrade, we make sure that we unsub from the upgraded instance
            entityTargetComponents = GetEntityComponents<IEntityTargetComponent>().OrderBy(comp => comp.Priority).ToArray();
            idleEntityTargetComponents = new HashSet<IEntityTargetComponent>(entityTargetComponents.Length);
            EntityTargetComponents = entityTargetComponents.ToDictionary(comp => comp.Code);

            EntityTargetProgressComponents = GetEntityComponents<IEntityTargetProgressComponent>().ToDictionary(comp => comp.Code);

            attackComponents = GetEntityComponents<IAttackComponent>().OrderBy(comp => comp.Priority).ToArray();
            AttackComponentsDic = attackComponents.ToDictionary(comp => comp.Code);

            // Minimap Icon
            MinimapIconHandler = GetComponentInChildren<IEntityMinimapIconHandler>();
        }

        protected virtual void Disable (bool isUpgrade, bool isFactionUpdate)
        {
            SetIdle();

            // Only completely disable components in case of a complete destruction of the entity
            // Because disabling a component = no going back from there, it is not functional anymore
            if(!isFactionUpdate)
                DisableComponents();
        }

        private void OnDestroy()
        {
            UnsubFromEvents();
        }
        #endregion

        #region Handling Events
        private void HandleEntityDead(IEntity sender, DeadEventArgs e)
        {
            Disable(e.IsUpgrade, false);
        }

        private void HandleEntitySelected(IEntity sender, EntitySelectionEventArgs args)
        {
            SelectionMarker.StopFlash(); //in case the selection texture of the entity was flashing

        }
        #endregion

        #region Updating IEntityTargetComponent Components State (Except Movement and Attack).
        public ErrorMessage SetTargetFirst (SetTargetInputData input)
        {
            if (!input.isMoveAttackRequest && this.IsLocalPlayerFaction())
                input.isMoveAttackRequest = (attackMgr.CanAttackMoveWithKey && input.playerCommand);

            if (CanAttack && FirstActiveAttackComponent.IsTargetValid(input) == ErrorMessage.none)
            {
                if(TasksQueue.IsValid() && TasksQueue.CanAdd(input))
                {
                    return TasksQueue.Add(new SetTargetInputData 
                    {
                        componentCode = FirstActiveAttackComponent.Code,

                        target = input.target,
                        playerCommand = input.playerCommand,

                        includeMovement = input.includeMovement
                    });
                }

                return attackMgr.LaunchAttack(
                    new LaunchAttackData<IEntity>
                    {
                        source = this,
                        targetEntity = input.target.instance as IFactionEntity,
                        targetPosition = input.target.instance.IsValid() ? input.target.instance.transform.position : input.target.position,
                        playerCommand = input.playerCommand,
                        isMoveAttackRequest = input.isMoveAttackRequest
                    });
            }
            else if (input.includeMovement && CanMove(input.playerCommand))
            {
                if(TasksQueue.IsValid() && TasksQueue.CanAdd(input))
                {
                    return TasksQueue.Add(new SetTargetInputData 
                    {
                        componentCode = MovementComponent.Code,

                        target = input.target,
                        playerCommand = input.playerCommand,

                        includeMovement = input.includeMovement
                    });
                }

                return mvtMgr.SetPathDestination(
                    new SetPathDestinationData<IEntity>
                    {
                        source = this,
                        destination = input.target.position,
                        offsetRadius = input.target.instance.IsValid() ? input.target.instance.Radius : 0.0f,
                        target = input.target.instance,
                        mvtSource = new MovementSource
                        {
                            playerCommand = input.playerCommand,
                            isMoveAttackRequest = input.isMoveAttackRequest
                        }
                    });
            }
            else
            {
                ErrorMessage notInvalidErrorMsg = ErrorMessage.invalid;
                foreach (IEntityTargetComponent comp in entityTargetComponents)
                {
                    if (comp.IsActive
                        && comp != MovementComponent
                        && comp != FirstActiveAttackComponent)
                    {
                        if(comp.IsTargetValid(input, out ErrorMessage lastErrorMsg))
                            return comp.SetTarget(input);

                        if (lastErrorMsg != ErrorMessage.invalid && lastErrorMsg != ErrorMessage.uninteractable)
                            notInvalidErrorMsg = lastErrorMsg;
                    }
                }

                if (input.playerCommand && RTSHelper.IsLocalPlayerFaction(this) && notInvalidErrorMsg != ErrorMessage.none)
                {
                    playerMsgHandler.OnErrorMessage(new PlayerErrorMessageWrapper
                    {
                        message = notInvalidErrorMsg,

                        source = this,
                        target = input.target.instance
                    });
                    return notInvalidErrorMsg;
                }

            }

            return ErrorMessage.failed;
        }

        public void SetIdle(bool includeMovement = true)
            => SetIdle(source: null, includeMovement);
        public void SetIdle(IEntityTargetComponent source, bool includeMovement = true)
        {
            for (int i = 0; i < entityTargetComponents.Length; i++)
            {
                IEntityTargetComponent nextComp = entityTargetComponents[i];
                if (source != nextComp
                    && (!source.IsValid() || nextComp.CanStopOnSetIdleSource(source))
                    && !nextComp.IsIdle
                    && (includeMovement || nextComp != MovementComponent))
                {
                    nextComp.Stop();
                }
            }
        }

        private void HandleEntityTargetComponentTargetUpdated(IEntityTargetComponent component, TargetDataEventArgs args)
        {
            if (component.IsIdle)
                return;

            idleEntityTargetComponents.Remove(component);
            if (isIdle)
                RaiseEntityExitIdle();
        }

        private void HandleEntityTargetComponentTargetStop(IEntityTargetComponent component, TargetDataEventArgs args)
        {
            if (idleEntityTargetComponents.Contains(component))
                return;

            idleEntityTargetComponents.Add(component);
            if(!isIdle && idleEntityTargetComponents.Count == entityTargetComponents.Length)
                RaiseEntityEnterIdle();
        }
        #endregion

        #region Updating Faction
        public abstract ErrorMessage SetFaction(IEntity source, int targetFactionID);

        public abstract ErrorMessage SetFactionLocal(IEntity source, int targetFactionID);
        #endregion

        #region Updating Entity Colors
        protected abstract void UpdateSelectionColor();
        #endregion

        #region IEquatable Implementation
        public bool Equals(IEntity other)
        {
#pragma warning disable CS0252 // Possible unintended reference comparison; left hand side needs cast
            return other == this;
#pragma warning restore CS0252 // Possible unintended reference comparison; left hand side needs cast
        }
        #endregion

        #region Editor Only
#if UNITY_EDITOR
        protected virtual void OnDrawGizmosSelected()
        {
            //Draw the entity's radius in blue
            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(transform.position, radius);
        }
#endif
        #endregion
    }
}
