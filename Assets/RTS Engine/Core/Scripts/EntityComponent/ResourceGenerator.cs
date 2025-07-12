﻿using System.Collections.Generic;

using UnityEngine;
using UnityEngine.Events;

using RTSEngine.UI;
using RTSEngine.Entities;
using RTSEngine.ResourceExtension;
using RTSEngine.Determinism;
using RTSEngine.Event;
using RTSEngine.Audio;
using RTSEngine.Utilities;
using RTSEngine.Logging;

namespace RTSEngine.EntityComponent
{

    /// <summary>
    /// Allows a FactionEntity instance (can be a unit or a building) to generate resources.
    /// </summary>
    public class ResourceGenerator : EntityComponentBase, IResourceGenerator 
    {
        #region Attributes
        [HideInInspector]
        public Int2D tabID = new Int2D {x = 0, y = 0};

        /*
         * Action types and their parameters:
         * generateResources: no parameters.
         * collectResources: no parameters.
         * */
        public enum ActionType : byte { generateResources, collectResources }

        //the FactionEntity instance to which this component is attached to.
        public IFactionEntity FactionEntity { private set; get; }

        [SerializeField, Tooltip("Duration (in seconds) required to generate resources.")]
        private float period = 1.0f;
        private TimeModifiedTimer timer;

        [SerializeField, Tooltip("Resources to generate every period."), Space(10)]
        private ResourceInput[] resources = new ResourceInput[0];
        public IReadOnlyList<ResourceInput> Resources => resources;

        // Holds the amount of the currently generated resources.
        private ModifiableResourceTypeValue[] generatedResources = new ModifiableResourceTypeValue[0];

        [SerializeField, Tooltip("Required resources to generate the above resources during each period.")]
        private ResourceInput[] requiredResources = new ResourceInput[0];

        // If a resource type is inclued in "resources" but not here then it will be assumed that it does not require to reach a threshold.
        [SerializeField, Tooltip("Threshold of generated resources required to achieve so that the resources are collectable by the player.")]
        private ResourceInput[] collectionThreshold = new ResourceInput[0];

        // For direct access to the resources threshold.
        private Dictionary<string, ResourceTypeValue> collectionThresholdDic = new Dictionary<string, ResourceTypeValue>();

        // Have the generated resources hit the target threshold?
        private bool isThresholdMet = false;
        [SerializeField, Tooltip("Stop generating resources when the target threshold is met?")]
        private bool stopGeneratingOnThresholdMet = false;

        [SerializeField, Tooltip("Automatically add resources to the player's faction when the threshold is met?")]
        private bool autoCollect = false;

        // For a local player resource generator, we only show the error for reaching maximum limit capacity for a resource once when it hits the limit capacity
        // When the capacity is increased and the resource generator is allowed to add resources to the faction, this will be set to false so that error can be shown again if the limit is reached again
        private bool hasShownLimitCapacityError = false;

        [SerializeField, Tooltip("What audio clip to play when the player collects resources produced by this generator?"), Space(10)]
        private AudioClipFetcher collectionAudio = new AudioClipFetcher();

        [SerializeField, Tooltip("Information used to display the resource collection task in case it is manaully collected by the player.")]
        private EntityComponentTaskUIAsset collectionTaskUI = null;

        // Game services
        protected IResourceManager resourceMgr { private set; get; }
        protected IGlobalEventPublisher globalEvent { private set; get; }
        protected IGameAudioManager audioMgr { private set; get; } 

        [SerializeField, Tooltip("Event triggered when the resource generator hits their collection threshold."), Space(10)]
        private UnityEvent onThresholdMet = new UnityEvent();

        [SerializeField, Tooltip("Event triggered when the resource generator's generated resources are collected.")]
        private UnityEvent onCollected = new UnityEvent();

        [SerializeField, Tooltip("Event triggered when the required resources for the generator are not available.")]
        private UnityEvent onRequirementMissing = new UnityEvent();
        #endregion

        #region Raising Events
        #endregion

        #region Initialization/Termination
        /// <summary>
        /// Initializer method required for each entity component that gets called by the Entity instance that the component is attached to.
        /// </summary>
        /// <param name="gameMgr">Active instance of the GameManager component.</param>
        /// <param name="entity">Entity instance that the component is attached to.</param>
        protected override void OnInit()
        {
            this.resourceMgr = gameMgr.GetService<IResourceManager>();
            this.globalEvent = gameMgr.GetService<IGlobalEventPublisher>();
            this.audioMgr = gameMgr.GetService<IGameAudioManager>(); 

            this.FactionEntity = Entity as IFactionEntity;

            // Assign an empty list of the generated resources
            generatedResources = new ModifiableResourceTypeValue[resources.Length];
            for (int i = 0; i < generatedResources.Length; i++)
            {
                generatedResources[i] = new ModifiableResourceTypeValue();
            }

            collectionThresholdDic.Clear();
            // Populate the collection threshold dictionary for easier direct access later when collecting resources
            foreach (ResourceInput ri in collectionThreshold)
                collectionThresholdDic.Add(ri.type.Key, ri.value);

            // Initial settings
            timer = new TimeModifiedTimer(period);
            isThresholdMet = false;
            hasShownLimitCapacityError = false;

            globalEvent.RaiseResourceGeneratorInitGlobal(this);
        }
        #endregion

        #region Handling Component Upgrade
        public override void HandleComponentUpgrade (IEntityComponent sourceEntityComponent)
        {
            ResourceGenerator sourceResourceGenerator = sourceEntityComponent as ResourceGenerator;
            if (!sourceResourceGenerator.IsValid())
                return;

            CollectResourcesAction(playerCommand: false);
        }
        #endregion

        #region Handling Actions
        public override ErrorMessage LaunchActionLocal(byte actionID, SetTargetInputData input)
        {
            switch((ActionType)actionID)
            {
                case ActionType.generateResources:

                    return GeneratePeriodResourcesActionLocal(input.playerCommand);

                case ActionType.collectResources:

                    return CollectResourcesActionLocal(input.playerCommand);

                default:
                    return base.LaunchActionLocal(actionID, input);
            }
        }
        #endregion

        #region Resource Generation Action
        /// <summary>
        /// Updates the generated resources.
        /// </summary>
        private void Update()
        {
            // Only allow the master instance to run the timers for generating resources
            if (!IsInitialized
                || !FactionEntity.CanLaunchTask 
                || !IsActive 
                || (stopGeneratingOnThresholdMet && isThresholdMet))
                return;

            // Turn into Action
            // Generating Resources:
            if (timer.ModifiedDecrease())
                // Directly generate resources locally on all client instances if it is a multiplayer game
                // Because sending every resource generation increase over the network is expensive and in this case, useless
                // since the collection will only be performed by the master instance
                // This also ensures that the resource generation is not halted by delay due to sending data over the network
                GeneratePeriodResourcesActionLocal(playerCommand: false); 
        }

        private ErrorMessage GeneratePeriodResourcesAction(bool playerCommand)
        {
            return LaunchAction(
                (byte)ActionType.generateResources, 
                new SetTargetInputData { playerCommand = playerCommand });
        }

        /// <summary>
        /// Generates the resources for one period.
        /// </summary>
        private ErrorMessage GeneratePeriodResourcesActionLocal(bool playerCommand)
        {
            if (!resourceMgr.HasResources(requiredResources, FactionEntity.FactionID))
            {
                onRequirementMissing.Invoke();
                return ErrorMessage.taskMissingResourceRequirements;
            }

            // Assume that the target threshold is met:
            isThresholdMet = true;

            for (int i = 0; i < generatedResources.Length; i++)
            {
                generatedResources[i].UpdateValue(resources[i].value);

                // One of the resources haven't met the threshold yet? => threshold not met
                if (isThresholdMet
                    && collectionThresholdDic.TryGetValue(resources[i].type.Key, out ResourceTypeValue thresholdValue)
                    && !generatedResources[i].Has(thresholdValue))
                    isThresholdMet = false;
            }

            // If the threshold is met and we can either autocollect the resources or this is a NPC faction:
            if (isThresholdMet && RTSHelper.IsMasterInstance() && (autoCollect || FactionEntity.IsNPCFaction()))
                CollectResourcesAction(playerCommand: false);

            if (isThresholdMet)
            {
                onThresholdMet.Invoke();

                globalEvent.RaiseEntityComponentTaskUIReloadRequestGlobal(this);
            }
                
            // Consume the required resources per period:
            resourceMgr.UpdateResource(FactionEntity.FactionID, requiredResources, add:false);

            timer.Reload();

            return ErrorMessage.none;
        }
        #endregion

        #region Resource Collection Action
        /// <summary>
        /// Collects all the generated resources.
        /// </summary>
        /// <param name="playerCommand">True if the player clicked on the resource collection task, otherwise false.</param>
        private ErrorMessage CollectResourcesAction(bool playerCommand)
        {
            return LaunchAction(
                (byte)ActionType.collectResources, 
                new SetTargetInputData { playerCommand = playerCommand });
        }

        private ErrorMessage CollectResourcesActionLocal(bool playerCommand)
        {
            // We no longer meet the threshold.
            isThresholdMet = false; 

            for (int i = 0; i < generatedResources.Length; i++)
            {
                var nextInput = new ResourceInput
                    {
                        type = resources[i].type,
                        value = new ResourceTypeValue
                        {
                            amount = generatedResources[i].Amount,
                            capacity = generatedResources[i].Capacity
                        }
                    };


                resourceMgr.UpdateResource(
                    FactionEntity.FactionID,
                    nextInput,
                    add:true,
                    out int restAmount);

                // In case not all of the amount could be added due to a limited capacity type resource that reached its maximum capacity...
                if(restAmount > 0)
                {
                    if (FactionEntity.IsLocalPlayerFaction() && !hasShownLimitCapacityError)
                    {
                        playerMsgHandler.OnErrorMessage(new PlayerErrorMessageWrapper
                        {
                            message = ErrorMessage.resourceTypeLimitCapacityReached,
                            amount = restAmount,
                            text = nextInput.type.DisplayName
                        });

                        hasShownLimitCapacityError = true;
                    }
                }
                else
                {
                    hasShownLimitCapacityError = false;
                }

                globalEvent.RaiseResourceGeneratorCollectedGlobal(this, new ResourceAmountEventArgs(nextInput));

                //and reset them.
                generatedResources[i].Reset(restAmount);
            }

            onCollected.Invoke();

            if(playerCommand && Entity.IsLocalPlayerFaction())
                audioMgr.PlaySFX(collectionAudio.Fetch(), FactionEntity, loop:false);

            return ErrorMessage.none;
        }
        #endregion

        #region Task UI
        protected override bool OnTaskUICacheUpdate(List<EntityComponentTaskUIAttributes> taskUIAttributesCache, List<string> disabledTaskCodesCache)
        {
            return RTSHelper.OnSingleTaskUIRequest(
                this,
                taskUIAttributesCache,
                disabledTaskCodesCache,
                collectionTaskUI,
                showCondition: !autoCollect && isThresholdMet);
        }

        /// <summary>
        /// Called when the player clicks on the resource collection task by the TaskUI instance that handles that task.
        /// </summary>
        /// <param name="task">TaskUI instance of the resource collection task. In other more complex components, multiple tasks can be drawn from the same component, this allows to define which task has been clicked.</param>
        public override bool OnTaskUIClick(EntityComponentTaskUIAttributes taskAttributes)
        {
            if (collectionTaskUI.IsValid() && taskAttributes.data.code == collectionTaskUI.Key)
            {
                CollectResourcesAction(playerCommand: true);
                return true;
            }

            return false;
        }
        #endregion
    }
}
