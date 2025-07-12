﻿using System.Collections.Generic;
using System.Linq;
using System;

using UnityEngine;
using UnityEngine.SceneManagement;

using RTSEngine.Event;
using RTSEngine.UI;
using RTSEngine.Determinism;
using RTSEngine.Faction;
using RTSEngine.Audio;
using RTSEngine.Logging;
using RTSEngine.Service;
using RTSEngine.Utilities;

namespace RTSEngine.Game
{
    public class GameManager : MonoBehaviour, IGameManager
    {
        #region Attributes
        [SerializeField, Tooltip("Scene to load when leaving this map. This is overwritten if the map is loaded through a lobby.")]
        private string prevScene = "MainMenu";

        [SerializeField, Tooltip("Code that identifies the game version/type of the map scene.")]
        private string gameCode = "2022.0.0";
        public string GameCode => gameCode;

        [SerializeField, Tooltip("Default faction defeat condition to determine how to win/lose this map. This is overwritten if the map is loaded through a lobby.")]
        private DefeatConditionType defeatCondition = DefeatConditionType.eliminateMain;
        public DefeatConditionType DefeatCondition => defeatCondition;

        public GameStateType State { private set; get; } = GameStateType.ready;

        [SerializeField, Tooltip("Time (in seconds) after the game starts, during which no faction can attack another.")]
        private float peaceTimeDuration = 60.0f; 
        public TimeModifiedTimer PeaceTimer { private set; get; }
        public bool InPeaceTime => PeaceTimer.CurrValue > 0.0f;

        [SerializeField, Tooltip("Audio clip played when the local player wins the game.")]
        private AudioClipFetcher winGameAudio = null;
        [SerializeField, Tooltip("Audio clip played when the local player loses the game.")]
        private AudioClipFetcher loseGameAudio = null;

        [Header("Faction Slots")]
        [SerializeField, Tooltip("Each element represents a faction slot that can be filled by a player or AI.")]
        private List<FactionSlot> factionSlots = new List<FactionSlot>();
        private List<IFactionSlot> activeFactionSlots;
        public IReadOnlyList<IFactionSlot> ActiveFactionSlots => activeFactionSlots.AsReadOnly();
        public int ActiveFactionCount => activeFactionSlots.Count;
        public IReadOnlyList<IFactionSlot> FactionSlots => factionSlots.Cast<IFactionSlot>().ToList();
        public IFactionSlot GetFactionSlot(int ID) => ID.IsValidIndex(factionSlots) ? factionSlots[ID] : null;
        public int FactionCount => factionSlots.Count;
        public IFactionSlot LocalFactionSlot {private set; get;}
        public int LocalFactionSlotID => LocalFactionSlot.IsValid() ? LocalFactionSlot.ID : -1;

        [Space(), SerializeField, Tooltip("Enable to allow randomizing the slot that each player gets.")]
        private bool randomFactionSlots = true;

        // Services
        protected IGameLoggingService logger { private set; get; }
        protected IInputManager inputMgr { private set; get; }
        protected ITimeModifier timeModifier { private set; get; }
        protected IGlobalEventPublisher globalEvent { private set; get; }
        protected IGameAudioManager audioMgr { private set; get; }
        protected IGameUITextDisplayManager gameUIText { private set; get; } 

        // Builder that sets up the game for this map.
        public IGameBuilder CurrBuilder { private set; get; }
        public bool ClearDefaultEntities { private set; get; }
#endregion

        #region Services Publisher
        private IReadOnlyDictionary<Type, IPreRunGameService> preRunServices = null;
        private IReadOnlyDictionary<Type, IPostRunGameService> postRunServices = null;

        T IServicePublisher<IPreRunGameService>.GetService<T>()
        {
            return TryGetService<T>(isPreService: true);
        }

        T IServicePublisher<IPostRunGameService>.GetService<T>()
        {
            return TryGetService<T>(isPreService: false);
        }

        private T TryGetService<T>(bool isPreService = false, bool requireValid = true)
        {
            Type type = typeof(T);
            T service = default;
            if (isPreService)
            {
                if (preRunServices.TryGetValue(type, out IPreRunGameService value))
                    service = (T)value;
            }
            else
            {
                if (postRunServices.TryGetValue(type, out IPostRunGameService value))
                    service = (T)value;
            }

            if(requireValid && !service.IsValid())
            {
                if (typeof(IOptionalGameService).IsAssignableFrom(type))
                    logger.LogWarning($"[GameManager] No service of type '{type}' has been registered! This is just a warning because the service is marked as optional!");
                else
                    logger.LogError($"[GameManager] No service of type '{type}' has been registered!");
            }

            return service;
        }
        #endregion

        #region Raising Events
        public event CustomEventHandler<IGameManager, EventArgs> GameServicesInitialized;
        private void RaiseGameServicesInitialized ()
        {
            var handler = GameServicesInitialized;
            handler?.Invoke(this, EventArgs.Empty);
        }

        public event CustomEventHandler<IGameManager, EventArgs> GameBuilt;
        private void RaiseGameBuilt()
        {
            var handler = GameBuilt;
            handler?.Invoke(this, EventArgs.Empty);
        }

        public event CustomEventHandler<IGameManager, EventArgs> GamePostBuilt;
        private void RaiseGamePostBuilt()
        {
            var handler = GamePostBuilt;
            handler?.Invoke(this, EventArgs.Empty);
        }

        public event CustomEventHandler<IGameManager, EventArgs> GameStartRunning;
        private void RaiseGameStartRunning()
        {
            var handler = GameStartRunning;
            handler?.Invoke(this, EventArgs.Empty);
        }
        #endregion

        #region Initializing/Terminating
        private void Awake()
        {
            IEnumerable<IGameBuilder> GameBuilders = DontDestroyOnLoadManager
                .AllDdolObjects
                .Select(obj => obj.GetComponent<IGameBuilder>());

            if(GameBuilders.Count() > 1)
            {
                logger.LogError($"[{GetType().Name}] There is more than one '{typeof(IGameBuilder).Name}' instance in the scene and that is not allowed. A maximum of one instance is allowed! Game will not start!");
                return;
            }

            CurrBuilder = GameBuilders.SingleOrDefault(obj => obj.IsValid());

            ClearDefaultEntities = CurrBuilder.IsValid() && CurrBuilder.ClearDefaultEntities;
            if(CurrBuilder.IsValid())
                gameCode = CurrBuilder.GameCode;

            // Services might need the functions in the RTSHelper so it makes sense to have it initialized before the services do.
            // It is a static class and not represented as a service as it does not hold any state expect the current active instance of the IGameManager.
            RTSHelper.Init(this);

            // Get services:
            this.logger = GetComponentInChildren<IGameLoggingService>();
            this.inputMgr = GetComponentInChildren<IInputManager>();
            this.timeModifier = GetComponentInChildren<ITimeModifier>();
            this.globalEvent = GetComponentInChildren<IGlobalEventPublisher>();
            this.audioMgr = GetComponentInChildren<IGameAudioManager>();
            this.gameUIText = GetComponentInChildren<IGameUITextDisplayManager>(); 

            SetState(GameStateType.ready);

            // Register the services when the game starts.
            var registeredServices = new Dictionary<Type, IMonoBehaviour>();
            preRunServices = GetComponentsInChildren<IPreRunGameService>()
                .ToDictionary(service =>
                {
                    Type serviceType = service is IPreRunGamePriorityService
                    ? service.GetType().GetSuperInterfaceType<IPreRunGamePriorityService>()
                    : service.GetType().GetSuperInterfaceType<IPreRunGameService>();

                    if (registeredServices.TryGetValue(serviceType, out var serviceObject))
                    {
                        logger.LogError($"[{GetType().Name}] Service with type '{serviceType}' has more than one instance on game objects: '{service.gameObject.name}' && '{serviceObject.gameObject.name}', only one is allowed!", serviceObject);
                        return null;
                    }

                    registeredServices.Add(serviceType, service);
                    return serviceType;
                },
                service => service);

            registeredServices.Clear();
            postRunServices = GetComponentsInChildren<IPostRunGameService>()
                .ToDictionary(service =>
                {
                    Type serviceType = service.GetType().GetSuperInterfaceType<IPostRunGameService>();

                    if (registeredServices.TryGetValue(serviceType, out var serviceObject))
                    {
                        logger.LogError($"[{GetType().Name}] Service with type '{serviceType}' has more than one instance on game objects: '{service.gameObject.name}' && '{serviceObject.gameObject.name}', only one is allowed!", serviceObject);
                        return null;
                    }

                    registeredServices.Add(serviceType, service);
                    return serviceType;
                },
                service => service);

            // Initialize pre run services.
            foreach (IPreRunGameService service in preRunServices
                .Values
                .OrderBy(service => service is IPreRunGamePriorityService ? (service as IPreRunGamePriorityService).ServicePriority : Mathf.Infinity))
                service.Init(this);

            RaiseGameServicesInitialized();

            if(!logger.RequireTrue(Build(),
                $"[{GetType().Name}] Unable to build and start game due to logged errors."))
                return;

            SetLocalPlayerFactionSlot();

            CurrBuilder?.OnGameBuilt(this);
            RaiseGameBuilt();

            PeaceTimer = new TimeModifiedTimer(peaceTimeDuration);

            RaiseGameStartRunning();

            SetState(GameStateType.running);

            // Subscribe to events
            globalEvent.FactionSlotDefeatConditionTriggeredGlobal += HandleFactionSlotDefeatConditionTriggeredGlobal;

            // Initialize post run services.
            foreach (IPostRunGameService service in postRunServices.Values)
                service.Init(this);

            RaiseGamePostBuilt();

            OnInit();
        }

        private bool Build ()
        {
            activeFactionSlots = new List<IFactionSlot>();

            // No pre defined builder? Use the default game settings!
            if(!CurrBuilder.IsValid())
            {
                RandomizeFactionSlots();

                int i = 0;
                while(i < factionSlots.Count)
                {
                    if(!factionSlots[i].Enabled)
                    {
                        factionSlots[i].InitDestroy();
                        factionSlots.RemoveAt(i);
                        continue;
                    }

                    factionSlots[i].Init(factionSlots[i].Data, ID: i, this);
                    activeFactionSlots.Add(factionSlots[i]);
                    i++;
                }

                return true;
            }

            RandomizeFactionSlots(CurrBuilder.Data.factionSlotIndexSeed?.ToList());

            defeatCondition = CurrBuilder.Data.defeatCondition;

            if(CurrBuilder.FactionSlotCount > FactionCount)
            {
                Debug.LogError($"[GameManager] Game Builder is attempting to initialize {CurrBuilder.FactionSlotCount} slots while there are only {FactionCount} slots available!");
                return false; 
            }

            for (int i = 0; i < CurrBuilder.FactionSlotCount; i++)
            {
                factionSlots[i].Init(CurrBuilder.FactionSlotDataSet.ElementAt(i), ID:i, this);
                activeFactionSlots.Add(factionSlots[i]);
            }

            // Remove the extra unneeded slots
            while (CurrBuilder.FactionSlotCount < factionSlots.Count)
            {
                factionSlots[factionSlots.Count - 1].InitDestroy();
                factionSlots.RemoveAt(factionSlots.Count - 1);
            }

            return true;
        }

        protected virtual void OnInit() { }

        private void OnDestroy()
        {
            globalEvent.FactionSlotDefeatConditionTriggeredGlobal -= HandleFactionSlotDefeatConditionTriggeredGlobal;
        }
        #endregion

        #region Setting Local Player Faction
        private bool SetLocalPlayerFactionSlot()
        {
            try
            {
                LocalFactionSlot = FactionSlots.SingleOrDefault(slot => slot.Data.isLocalPlayer);
            }
            catch (InvalidOperationException e)
            {
                logger.LogError($"[{GetType().Name}] Unable to find a single faction slot marked as the local player. Exception: {e.Message}");
            }

            if (!logger.RequireValid(LocalFactionSlot,
                    $"[{GetType().Name}] There is either no local faction slot or there are more than one. The 'LocalFactionSlot' property will be set to null to avoid any unwanted behaviour, please handle controls independently using the faction slots data. If this is intended, ignore this warning.",
                    source: this,
                    type: LoggingType.warning))
            {
                LocalFactionSlot = FactionSlots.FirstOrDefault(slot => slot.Data.isLocalPlayer);
                return false;
            }

            return true;
        }
        #endregion

        #region Handling Peace Time
        public void SetPeaceTime(float time)
        {
            PeaceTimer.Reload(time);
        }

        void Update()
        {
            if (State != GameStateType.running)
                return;

            if(PeaceTimer.ModifiedDecrease())
                enabled = false;
        }
        #endregion

        #region Handling Faction Slot Randomizing
        private void RandomizeFactionSlots()
        {
            RandomizeFactionSlots(RTSHelper.GenerateRandomIndexList(FactionCount));
        }

        private void RandomizeFactionSlots(IReadOnlyList<int> indexSeedList)
        {
            if (!randomFactionSlots
                || !logger.RequireTrue(indexSeedList.IsValid() && indexSeedList.Count == factionSlots.Count,
                $"[{GetType().Name}] Unable to randomize faction slots due to an index seed list that does not match with the faction slots count. Faction slots will not be randomized!",
                type: LoggingType.warning))
                return;

            int i = 0;
            while(i < indexSeedList.Count) 
            {
                if (i == indexSeedList[i] || i > indexSeedList[i]) 
                {
                    i++;
                    continue;
                }

                var tempSlot = factionSlots[i];
                factionSlots[i] = factionSlots[indexSeedList[i]];
                factionSlots[indexSeedList[i]] = tempSlot;

                i++;
            }
        }
        #endregion

        #region Handling Defeat Condition / Faction Defeat
        private void HandleFactionSlotDefeatConditionTriggeredGlobal(IFactionSlot slot, DefeatConditionEventArgs args)
        {
            if (args.Type == DefeatCondition)
                OnFactionDefeated(slot.ID);
        }

        public ErrorMessage OnFactionDefeated(int factionID)
        {
            return inputMgr.SendInput(new CommandInput()
            {
                sourceMode = (byte)InputMode.faction,
                targetMode = (byte)InputMode.factionDestroy,

                intValues = inputMgr.ToIntValues(factionID),
            });
        }

        public ErrorMessage OnFactionDefeatedLocal(int factionID)
        {
            if (!factionSlots[factionID].IsActiveFaction())
                return ErrorMessage.inactive;

            factionSlots[factionID].UpdateState(FactionSlotState.eliminated);
            factionSlots[factionID].UpdateRole(FactionSlotRole.client);

            activeFactionSlots.Remove(factionSlots[factionID]);

            gameUIText.FactionSlotDefeatToText(factionSlots[factionID], out string defeatMsg);

            globalEvent.RaiseShowPlayerMessageGlobal(
                this,
                new MessageEventArgs
                (
                    type: MessageType.info,
                    message: defeatMsg
                ));

            globalEvent.RaiseFactionSlotDefeatedGlobal(factionSlots[factionID], new DefeatConditionEventArgs(type: DefeatCondition));

            if (LocalFactionSlot.IsValid() && State == GameStateType.running)
            {
                // If this is the local player rfaction?
                if (factionID.IsLocalPlayerFaction())
                    LooseGame();
                else if (factionSlots.Count(slot => slot.State == FactionSlotState.active) == 1)
                    WinGame();
            }

            return ErrorMessage.none;
        }
        #endregion

        #region Handling Local Player Winning/Losing Game
        public void WinGame()
        {
            audioMgr.PlaySFX(winGameAudio, null, loop:false);

            SetState(GameStateType.won);
        }

        public void LooseGame()
        {
            audioMgr.PlaySFX(loseGameAudio, null, loop:false);

            SetState(GameStateType.lost);
        }
        #endregion

        #region Handling Game State
        public void SetState (GameStateType newState)
        {
            State = newState;

            globalEvent.RaiseGameStateUpdatedGlobal();
        }
        #endregion

        #region Leaving Game
        public void LeaveGame()
        {
            if (CurrBuilder.IsValid())
            {
                CurrBuilder.OnGameLeave();
                return;
            }

            SceneManager.LoadScene(prevScene);
        }
        #endregion
    }
}
