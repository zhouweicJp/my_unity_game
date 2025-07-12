﻿using System;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;

using RTSEngine.Entities;
using RTSEngine.Event;
using RTSEngine.ResourceExtension;
using RTSEngine.Determinism;
using RTSEngine.Logging;
using RTSEngine.NPC.BuildingExtension;
using RTSEngine.NPC.UnitExtension;
using RTSEngine.NPC.EntityComponent;

namespace RTSEngine.NPC.ResourceExtension
{
    public class NPCCapacityResourceManager : NPCComponentBase, INPCCapacityResourceManager
    {
        #region Attributes 
        // Allow to have multiple instance of this component for each NPC faction
        public override bool IsSingleInstance => false;

        [SerializeField, Tooltip("Capacity resource type to be monitored by this component.")]
        private FactionTypeFilteredResourceType capacityResource = new FactionTypeFilteredResourceType();

        public ResourceTypeInfo TargetCapacityResource { get; private set; } = null;
        private IFactionResourceHandler capacityResourceHandler = null;

        [SerializeField, EnforceType(typeof(IFactionEntity)), Tooltip("List of potential faction entities (units and buildings) that can increase the capacity resource.")]
        private List<GameObject> factionEntities = new List<GameObject>();

        // Monitors the active faction entities that can update the capacity resource value.
        private NPCActiveRegulatorMonitor unitsMonitor;
        private NPCActiveRegulatorMonitor buildingsMonitor;

        [SerializeField, Tooltip("Target resource capacity range that the NPC faction will aim to reach as long as this component is active.")]
        private IntRange targetCapacityRange = new IntRange(40, 50);

        [SerializeField, Tooltip("How often does the NPC faction attempt to monitor the capacity resource to update it and reach its capacity goal?")]
        private FloatRange reloadRange = new FloatRange(10.0f, 15.0f);
        private TimeModifiedTimer timer;

        [SerializeField, Tooltip("Allow this component to automatically create faction entities to increase the resource capacity?")]
        private bool autoCreate = true;
        [SerializeField, ReadOnly, Tooltip("Capacity that is expected to be added from a building that is getting placed or a unit being created by the NPC faction")]
        private int pendingCapacity;
        [SerializeField, Tooltip("When the free amount of the capacity resource reaches this value, this component will attempt to create the faction entities that can raise the capacity.")]
        private int minFreeAmount = 3;

        // NPC Components
        protected INPCBuildingCreator npcBuildingCreator { private set; get; }
        protected INPCUnitCreator npcUnitCreator { private set; get; }
        protected INPCEntityComponentTracker npcEntityCompTracker { private set; get; }

        // services
        protected IResourceManager resourceMgr { private set; get; }
        #endregion

        #region Initializing/Terminating
        protected override void OnPreInit()
        {
            this.resourceMgr = gameMgr.GetService<IResourceManager>();

            this.npcBuildingCreator = npcMgr.GetNPCComponent<INPCBuildingCreator>();
            this.npcUnitCreator = npcMgr.GetNPCComponent<INPCUnitCreator>();
            this.npcEntityCompTracker = npcMgr.GetNPCComponent<INPCEntityComponentTracker>(); 

            unitsMonitor = new NPCActiveRegulatorMonitor(gameMgr, factionMgr);
            buildingsMonitor = new NPCActiveRegulatorMonitor(gameMgr, factionMgr);

            npcBuildingCreator.FirstBuildingCenterInitialized += HandleFirstBuildingCenterInitialized;
        }

        protected override void OnPostInit()
        {
            // Initial state:
            pendingCapacity = 0;
            timer = new TimeModifiedTimer(reloadRange.RandomValue);

            // Building events
            globalEvent.BuildingPlacementStartGlobal += HandleBuildingPlacementStartGlobal;
            globalEvent.BuildingPlacementStopGlobal += HandleBuildingPlacementStopOrBuiltGlobal;
            globalEvent.BuildingBuiltGlobal += HandleBuildingPlacementStopOrBuiltGlobal;
        }

        private void HandleFirstBuildingCenterInitialized(INPCBuildingCreator sender, EventArgs args)
        {
            // Activate the faction entities that influence the capacity resources
            // Only activate them at this point because if the NPC is using building centers to regulate building creation then the centers have to be initialized first.
            TargetCapacityResource = capacityResource.GetFiltered(gameMgr.GetFactionSlot(factionMgr.FactionID).Data.type);
            if (!logger.RequireValid(TargetCapacityResource,
                $"[{GetType().Name} - {factionMgr.FactionID}] No capacity resource that matches the faction type has been found, disabling component!",
                source: this,
                LoggingType.warning))
            {
                IsActive = false;
                return;
            }

            if (!logger.RequireTrue(TargetCapacityResource.HasCapacity,
                $"[{GetType().Name} - {factionMgr.FactionID}] Resource '{TargetCapacityResource.Key}' Must be a capacity resource to be handled by this component!"))
                return;

            // Capacity resource events
            capacityResourceHandler = resourceMgr.FactionResources[factionMgr.FactionID].ResourceHandlers[TargetCapacityResource];
            capacityResourceHandler.FactionResourceAmountUpdated += HandleFactionResourceAmountUpdated;

            // Add the faction entities that can influence the capacity resource managed by this component
            for (int i = 0; i < factionEntities.Count; i++)
            {
                IFactionEntity nextEntity = factionEntities[i].IsValid() ? factionEntities[i].GetComponent<IFactionEntity>() : null;
                if (!logger.RequireValid(nextEntity,
                    $"[{GetType().Name} - {factionMgr.FactionID}] 'Faction Entities' list has some unassigned or invalid elements.",
                    source: this))
                    continue;

                bool hasTargetCapacityResource = false;
                foreach(ResourceInput resourceInput in nextEntity.InitResources)
                {
                    if (resourceInput.type == TargetCapacityResource)
                    {
                        hasTargetCapacityResource = true;
                        break;
                    }
                }
                if (!hasTargetCapacityResource)
                    continue;

                if (nextEntity.IsUnit())
                {
                    NPCUnitRegulator nextUnitRegulator;
                    if ((nextUnitRegulator = npcUnitCreator.ActivateUnitRegulator(
                        nextEntity as IUnit)).IsValid())
                        unitsMonitor.AddCode(nextUnitRegulator.Prefab.Code);
                }
                else if (nextEntity.IsBuilding())
                {
                    NPCBuildingRegulator nextBuildingRegulator;
                    if ((nextBuildingRegulator = npcBuildingCreator.ActivateBuildingRegulator(
                        nextEntity as IBuilding,
                        npcBuildingCreator.FirstBuildingCenter)).IsValid())
                        buildingsMonitor.AddCode(nextBuildingRegulator.Prefab.Code);
                }
            }

            if (unitsMonitor.Count <= 0 && buildingsMonitor.Count <= 0)
                logger.LogWarning($"{GetType().Name} - {factionMgr.FactionID}] No regulators are found for faction entities that can increase the capacity resource when created.", source: this);
        }

        protected override void OnDestroyed()
        {
            npcBuildingCreator.FirstBuildingCenterInitialized -= HandleFirstBuildingCenterInitialized;

            if (TargetCapacityResource.IsValid())
            {
                IFactionResourceHandler capacityResourceHandler = resourceMgr.FactionResources[factionMgr.FactionID].ResourceHandlers[TargetCapacityResource];
                capacityResourceHandler.FactionResourceAmountUpdated -= HandleFactionResourceAmountUpdated;
            }

            globalEvent.BuildingPlacementStartGlobal -= HandleBuildingPlacementStartGlobal;
            globalEvent.BuildingPlacementStopGlobal -= HandleBuildingPlacementStopOrBuiltGlobal;
            globalEvent.BuildingBuiltGlobal -= HandleBuildingPlacementStopOrBuiltGlobal;

            buildingsMonitor.Disable();
            unitsMonitor.Disable();
        }
        #endregion

        #region Handling Events: Capacity Resource Handler
        private void HandleFactionResourceAmountUpdated(IFactionResourceHandler capacityResourceHandler, ResourceUpdateEventArgs args)
        {
            // If the amount of the capacity resources is less than required and has not reached the target capacity value
            // then activate this component to make the NPC component attempt to raise the capacity amount.
            if (autoCreate
                && capacityResourceHandler.FreeAmount + pendingCapacity < minFreeAmount
                && capacityResourceHandler.Capacity < targetCapacityRange.RandomValue)
                IsActive = true;
        }
        #endregion

        #region Handling Events: IBuilding
        private void HandleBuildingPlacementStartGlobal(IBuilding building, EventArgs e)
        {
            if (building.FactionID != factionMgr.FactionID)
                return;

            // Handle the potential of increasing population capactiy when the building is later placed by tracking its population resource increase
            pendingCapacity += GetResourceCapacityValue(building.InitResources);
        }

        //when the building is built (its init resources will be added to faction
        private void HandleBuildingPlacementStopOrBuiltGlobal(IBuilding building, EventArgs e)
        {
            if (building.FactionID != factionMgr.FactionID)
                return;

            pendingCapacity -= GetResourceCapacityValue(building.InitResources);
        }
        #endregion

        #region Creating Faction Entities
        protected override void OnActiveUpdate()
        {
            base.OnActiveUpdate();

            timer.ModifiedDecrease();

            if (timer.CurrValue <= 0.0f)
            {
                timer = new TimeModifiedTimer(reloadRange.RandomValue);

                if (IsTargetCapacityReached
                    || OnIncreaseCapacityRequest())
                    IsActive = false;
            }
        }

        public bool OnIncreaseCapacityRequest()
        {
            // Prioritize placing buildings to increase capacity resource.

            if (buildingsMonitor.Count > 0
                && npcBuildingCreator.OnCreateBuildingRequest(buildingsMonitor.RandomCode))
                return true;

            if (unitsMonitor.Count > 0)
                return npcUnitCreator.OnCreateUnitRequest(unitsMonitor.RandomCode, requestedAmount: 1, out _);

            return false;
        }
        #endregion

        #region Helper Methods
        private int GetResourceCapacityValue(IEnumerable<ResourceInput> resourceInputs)
        {
            foreach(ResourceInput resourceInput in resourceInputs)
            {
                if (resourceInput.type == TargetCapacityResource)
                    return resourceInput.value.capacity;
            }
            return 0;
        }

        public bool IsTargetCapacityReached => capacityResourceHandler.Capacity >= targetCapacityRange.RandomValue;
        #endregion

        #region Logging
        [System.Serializable]
        private struct NPCCapacityResourceLogData
        {
            public int currAmount;
            public int pendingCapacity;
            public int currCapacity;

            public string[] unitCodes;
            public string[] buildingCodes;
        }

        [SerializeField, ReadOnly]
        private NPCCapacityResourceLogData capacityResourceLogs = new NPCCapacityResourceLogData();

        protected override void UpdateActiveLogs()
        {
            if (!capacityResourceHandler.IsValid())
                return;

            capacityResourceLogs = new NPCCapacityResourceLogData
            {
                currAmount = capacityResourceHandler.Amount,
                currCapacity = capacityResourceHandler.Capacity,

                pendingCapacity = pendingCapacity,

                unitCodes = unitsMonitor.AllCodes.ToArray(),
                buildingCodes = buildingsMonitor.AllCodes.ToArray(),
            };
        }
        #endregion
    }
}
