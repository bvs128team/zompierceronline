using System;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    [BepInPlugin("local.zompiercer.lan", "Zompiercer LAN", LanPlugin.PluginVersion)]
    [DefaultExecutionOrder(10000)]
    public sealed class LanPlugin : BaseUnityPlugin
    {
        internal const string PluginVersion = "1.4.20";
        private static string NetworkGameVersion { get { return Application.version + "/LAN1.4.20"; } }
        private const int Port = 27777;
        private const float SendInterval = 0.10f;
        // 1.4.6: the player's state goes twice as often as the rest (20 a second), stamped with this clock.
        private const float StateInterval = 0.05f;
        private static readonly System.Diagnostics.Stopwatch StateClock = System.Diagnostics.Stopwatch.StartNew();
        private float _nextStateAt;
        private LanIsolatedSession _network;
        private LanIsolatedSession _pairing;
        private LanIsolatedSession _isolated;
        private LanIsolatedSession _roomBrowser;
        // Relay public room list (0.13.0): the host lists its room; a guest reads the list.
        private LanIsolatedSession _relayRooms;
        private bool _relayPublic, _hostedPublic;
        private float _relayRoomsAt = -10f;
        private Vector2 _relayRoomsScroll;
        private bool _roomSearchReported;
        private string _roomName = "Комната", _manualAddress = "";
        // Internet relay mode. The server comes from this build (RelayDefaults, masked)
        // or, for advanced players, from the Custom* config entries; never from the network.
        private ConfigEntry<bool> _useRelayEntry;
        private ConfigEntry<string> _customServerEntry, _customFingerprintEntry, _customKeyEntry;
        private bool _relayMode, _showJoinSecret;
        // Room secrets stay in memory only: never written to config or logs.
        private string _relayHostSecret = "", _relayJoinSecret = "", _relayRoomInput = "";
        private Vector2 _roomScroll, _panelScroll;
        private ZombieFighterController _lanHeldPlayer;
        private bool _lanPreviousControl, _lanPreviousCursor;
        private CursorLockMode _lanPreviousLock;
        private ZombieSync _zombies;
        // Guest profile persistence (0.5.0): the host stores, the guest restores and uploads.
        private LanGuestHost _guestHost;
        private LanGuestClient _guestClient;
        private LanPlayerIdentity _identity;
        private bool _identityFailed;
        // Shared storages (0.6.0): the host owns every container, the guest asks.
        private LanStorageHost _storageHost;
        private LanStorageClient _storageClient;
        // Scene doors (0.6.1): host-authoritative, guest asks.
        private LanWorldDoors _doors;
        private LanSceneStates _sceneStates; // 1.4.4
        private float _lostAt = -1f; // 1.4.5: guest: when the host was lost (-1: connected or never)
        private Func<string> _questGuestProfile; // 1.4.5
        // 1.2.0: the host's quests, shared with the guest.
        private LanQuests _quests;
        private Func<Vector3?> _questGuestPosition;
        private Func<int, long> _questGuestCount;
        private Func<int, uint, string, bool> _questRequest;
        // 1.2.1: barrels and robot dogs.
        private Func<Transform> _takableAvatar;
        private Func<string> _takableProfile;
        private Func<bool> _takableAble, _takableCrouch;
        // Loose items and loot bags of the host's world (0.7.0).
        private LanWorldItems _worldItems;
        private HostCombatInventory _combatInventory = new HostCombatInventory();
        private LanPeerWorkLimits _peerWorkLimits = new LanPeerWorkLimits();
        private float _remoteStateAt;
        private float _remoteMoveBudget, _remoteMoveBudgetAt, _remoteRejectedSince = -1f, _remoteRejectLogAt = -10f;
        private ConfigEntry<bool> _askBelongingsEntry, _friendDrivesEntry, _showPartnerEntry, _chatEnabledEntry, _chatHideEntry, _notifySoundEntry, _partnerHealthEntry;
        // 1.0.8: the partner's health from its states (LanProtocol.NoHealth: not known), and when
        // the host was last reminded of a friend waiting for permission.
        private byte _remoteHealth = LanProtocol.NoHealth;
        // 1.1.6: the partner's health points as its own bar shows them (max 0: unknown), the look it
        // chose (LanProtocol.NoSkin: the default) and the look of the avatar shown now.
        private ushort _remoteHealthNow, _remoteHealthMax;
        private byte _remoteSkin = LanProtocol.NoSkin, _avatarSkin = LanProtocol.NoSkin;
        private float _avatarCreatedAt = -100f;
        private ConfigEntry<string> _skinEntry;
        private bool _approvalNotified;
        private float _approvalRemindAt;
        private ConfigEntry<string> _markKeyEntry, _chatKeyEntry;
        private LanMarks _marks;
        private LanChat _chat;
        // Guest death (0.12.0): the death request is queued; host: when the guest died (-1: alive).
        private bool _deathSent, _deathLeftItems, _respawning;
        private float _deathRetryAt, _guestDeadAt = -1f;
        private Vector3 _remoteCombatOffset;
        private IPEndPoint _peer;
        private bool _hosting;
        private bool _connected;
        private bool _panelOpen;
        private bool _connectionPanelClosed;
        private float _panelContentHeight; // measured height of the panel's lines (1.0.4)
        private bool _joinStartedGame;
        private bool _startedLocalNewGame;
        // 1.1.6: the game's own end of a world load (character HUD on, cursor locked) is still due.
        private bool _nativeHudPending;
        private bool _clientPlacedNearHost;
        private float _lastSend;
        private float _lastReceive;
        private string _joinCode = "";
        private float _nextNetworkWarning;
        private string _status = "Не подключено";
        private string _remoteScene;
        private Vector3 _remotePosition;
        private float _remoteYaw;
        private float _remoteTrainPosition;
        private GameObject _avatar;
        private float _avatarYOffset;
        private Animator _avatarAnimator;
        private RemoteAvatarMotion _avatarMotion;
        private float _remoteSpeed; // the partner's ground speed, m/s, smoothed (1.0.9)
        private float _remotePitch; // where the partner looks up (+) or down, degrees (1.1.0)
        private bool _remoteCrouch, _remoteAim;
        // 1.1.9: the partner lies downed or dead, its flashlights, a reload; whether this player's
        // avatar of it lies; the Revive packets' sequence.
        private bool _remoteDowned, _remoteDead, _remoteHelmetLight, _remoteGunLight, _remoteReload, _avatarLying;
        private uint _reviveSequence;
        // 1.2.2: explosions for the guest, sleeping together, the partner on the map.
        private uint _explosionSequence, _sleepSequence;
        private bool _partnerHere;
        private Func<bool> _sleepPartner;
        private Action<byte, int> _sleepSend;
        private Action<byte, string, Vector3> _explosionSend;
        private Func<Vector3?> _mapPartner;
        // 1.3.0: the guest's checked stats and whether it plays in this world.
        private Func<LanGuestStats> _creditStats;
        private Func<bool> _creditHere;
        private RemoteAvatarExtras _avatarExtras;
        private ConfigEntry<string> _nameEntry;
        private ConfigEntry<float> _coopHealthEntry, _coopLootEntry; // 1.4.4
        private RemoteEquipmentVisual _remoteEquipment;
        private int _remoteEquippedItem = -1, _remoteShotItem = -1;
        private uint _remoteShotSequence;
        private float _readySince = -1f;
        private string _clientSavePath;
        private TrainSync _trainSync;
        private ulong _session;
        private uint _stateSequence, _remoteSequence;
        private bool _haveRemoteState;
        private int _remoteCarIndex = -1;
        private Vector3 _remoteLocalPosition;
        private float _remoteLocalYaw;
        private float _nextWeaponCheck;
        private string _lastWeaponState;
        private readonly PlayerPoseBuffer _poses = new PlayerPoseBuffer();
        private bool _inventoryReadyCaptured;
        private uint _worldEpoch, _worldSequence, _receivedWorldSequence, _clientLoadEpoch;
        private string _hostWorldScene;
        private bool _hostSawLoading, _hostWorldReady;
        private float _nextWorldSend, _hostReadySince = -1f, _nextTravelAttempt;
        private Packet _clientWorld;
        private ClientSceneTravel _travel;
        private readonly WorldClockSync _clock = new WorldClockSync();
        private static readonly FieldInfo LastGround = typeof(ZombieFighterController).GetField("LastKnownPositionOnGround", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo UndergroundTimer = typeof(ZombieFighterController).GetField("FallUndergroundTimer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private void Awake()
        {
            Logger.LogInfo("Zompiercer LAN " + PluginVersion + " loaded; AppContainer network worker, bounded IPC, J-PAKE and DTLS");
            _useRelayEntry = Config.Bind("Relay", "UseRelay", false, "Play through the internet relay instead of the local network");
            _askBelongingsEntry = Config.Bind("Guests", "AskBeforeAcceptingBelongings", false,
                "Host: ask before taking a new friend's belongings onto the ledger (false: accept them at once)");
            _friendDrivesEntry = Config.Bind("Guests", "FriendMayDrive", true,
                "Host: the friend may use the train's controls (engine, throttle, brake, reverse, lights, horn), repair broken levers and switch lights of built parts");
            LanTrainControl.HostAllows = _friendDrivesEntry.Value;
            _markKeyEntry = Config.Bind("Marks", "Key", "Mouse2",
                "Key that marks what is under the crosshair: Mouse2 (middle mouse button) or a keyboard key name such as T or V");
            _showPartnerEntry = Config.Bind("Marks", "ShowPartner", true, "Show where the other player is (name, distance, arrow at the screen edge)");
            KeyCode markKey;
            LanMarks.Key = Enum.TryParse(_markKeyEntry.Value, true, out markKey) ? markKey : KeyCode.Mouse2;
            LanMarks.ShowPartner = _showPartnerEntry.Value;
            _chatEnabledEntry = Config.Bind("Chat", "Enabled", true, "Text chat with the other player (plain text only, at most 200 characters)");
            _chatKeyEntry = Config.Bind("Chat", "Key", "Return", "Key that opens the chat line: Return (Enter) or a keyboard key name such as Y");
            _chatHideEntry = Config.Bind("Chat", "HidePartner", false, "Do not show the other player's chat messages");
            KeyCode chatKey;
            LanChat.Key = Enum.TryParse(_chatKeyEntry.Value, true, out chatKey) && chatKey != KeyCode.None && chatKey != KeyCode.Escape && chatKey < KeyCode.Mouse0 ? chatKey : KeyCode.Return;
            LanChat.Enabled = _chatEnabledEntry.Value;
            LanChat.HidePartner = _chatHideEntry.Value;
            _notifySoundEntry = Config.Bind("Notifications", "Sound", true,
                "Play a short sound when the other player connects or the connection is lost, when the other player writes in chat, and (host) when a friend waits for your permission to join");
            LanNotify.Sound = _notifySoundEntry.Value;
            _partnerHealthEntry = Config.Bind("Partner", "HealthBar", true, "Show the other player's health above them when you look at them from close by");
            _skinEntry = Config.Bind("Player", "Look", "", "How the other player sees you: a game NPC model name such as RegularWomanNPC_01 (empty: the default); chosen in the main menu");
            _nameEntry = Config.Bind("Player", "Name", "", "Your nickname for the other player (up to 16 characters, typed in the main menu next to the look; empty: Хост/Друг)");
            LanSkinPicker.Initialize(_skinEntry, _nameEntry, Logger.LogWarning);
            _coopHealthEntry = Config.Bind("Coop", "ZombieHealth", 1.3f, new ConfigDescription(
                "Host: zombie health multiplier while the friend is in your world (1 = as in your difficulty settings; never saved into the world)",
                new AcceptableValueRange<float>(LanDifficulty.MinFactor, LanDifficulty.MaxFactor)));
            _coopLootEntry = Config.Bind("Coop", "Loot", 1.5f, new ConfigDescription(
                "Host: how many items a container or a zombie's bag gets while a friend is connected (1 = as in your difficulty settings)",
                new AcceptableValueRange<float>(LanDifficulty.MinFactor, LanDifficulty.MaxFactor)));
            _customServerEntry = Config.Bind("Relay", "CustomServer", "",
                "Advanced: your own ZompiercerRelay as IPv4 or IPv4:port. Leave empty to use the server built into this mod build. Requires CustomFingerprint.");
            _customFingerprintEntry = Config.Bind("Relay", "CustomFingerprint", "",
                "Advanced: SHA-256 certificate fingerprint of your own relay, as printed by its installer (AB:CD:...).");
            _customKeyEntry = Config.Bind("Relay", "CustomAccessKey", "",
                "Advanced: access key of your own relay, if it has one. Stored here as plain text.");
            Logger.LogInfo("LAN startup: configuration read; migrating relay settings");
            MigrateLegacyRelaySettings();
            _relayMode = _useRelayEntry.Value;
            _travel = new ClientSceneTravel(Logger);
            // Keep the newest few backups, checkpoints and client session folders.
            LanRetention.Run(Paths.BepInExRootPath, null, Logger.LogInfo, Logger.LogWarning);
            Logger.LogInfo("LAN startup: installing game hooks");
            LanSaveIsolation.Initialize(Logger);
            LanGuestPersistence.Initialize(Logger);
            LanBeaverSave.Initialize(Logger);
            LanBeaverItem.Initialize(Logger.LogInfo);
            LanStorageGame.Initialize(Logger.LogWarning);
            LanWorldDoors.Initialize(Logger.LogWarning);
            LanQuests.Initialize(Logger.LogWarning);
            LanTakables.Initialize(Logger.LogWarning);
            LanRobotWindow.Initialize(Logger.LogWarning);
            LanExplosions.Initialize(Logger.LogWarning);
            LanThrows.Initialize(Logger.LogWarning);
            LanTrainDecor.Initialize(Logger.LogWarning);
            LanFuelStation.Initialize(Logger.LogWarning);
            LanSceneStates.Initialize(Logger.LogWarning);
            LanDifficulty.Initialize(Logger.LogInfo);
            LanBossReward.Initialize(Logger.LogInfo);
            LanLeave.Initialize(Logger.LogWarning);
            LanLeave.Leaving = LeaveToMenu;
            LanSleep.Initialize(Logger.LogInfo);
            LanSleep.GuestSleeps = hours => LanGuestCredits.Sleep(hours);
            LanGuestCredits.Initialize(Logger.LogWarning);
            LanMapMarker.Initialize(Logger.LogWarning);
            LanWorldItems.Initialize(Logger.LogWarning);
            LanWorldWork.Initialize(Logger.LogWarning);
            LanTrainBuild.Initialize(Logger.LogWarning);
            LanTrainControl.Initialize(Logger.LogWarning);
            LanBossArena.Initialize(Logger.LogInfo, Logger.LogWarning, text => _marks?.Toast(text));
            LanGuestDeath.Initialize(Logger.LogWarning);
            LanDowned.Initialize(Logger.LogWarning);
            LanDowned.PartnerCanHelp = PartnerCanHelp;
            LanDowned.PartnerDown = () => _connected && _haveRemoteState && _remoteDowned;
            LanDowned.PartnerAt = () => _connected && _haveRemoteState ? RemoteWorldPosition() : (Vector3?)null;
            LanDowned.Send = SendRevive;
            LanGuestDeath.Attached = () => !_hosting && _connected && _clientPlacedNearHost && _storageClient != null && LanSaveIsolation.Active;
            Logger.LogInfo("LAN startup: game hooks installed; starting background asset verification");
            LanWorldCatalog.Initialize(Application.dataPath);
            try { ClientTravelHooks.Init(); }
            catch (Exception ex) { Logger.LogError("LAN scene hooks unavailable: " + ex); }
            try { RemoteEquipment.Init(); }
            catch (Exception ex) { Logger.LogWarning("Remote weapon events unavailable: " + ex.Message); }
            Logger.LogInfo("LAN startup completed; network worker starts only on a LAN action");
        }

        private void OnDestroy() { _panelOpen = false; ReleaseLanInput(); StopNetwork(); _roomBrowser?.Dispose(); _relayRooms?.Dispose(); RemoteEquipment.Shutdown(); ClientTravelHooks.Shutdown(); LanSaveIsolation.Shutdown(); LanGuestPersistence.Shutdown(); LanStorageGame.Shutdown(); LanWorldDoors.Shutdown(); LanQuests.Shutdown(); LanTakables.Shutdown(); LanRobotWindow.Shutdown(); LanExplosions.Shutdown(); LanThrows.Shutdown(); LanTrainDecor.Shutdown(); LanFuelStation.Shutdown(); LanSceneStates.Shutdown(); LanDifficulty.Shutdown(); LanBossReward.Shutdown(); LanLeave.Shutdown(); LanSleep.Shutdown(); LanMapMarker.Shutdown(); LanGuestCredits.Shutdown(); LanWorldItems.Shutdown(); LanWorldWork.Shutdown(); LanTrainBuild.Shutdown(); LanTrainControl.Shutdown(); LanGuestDeath.Shutdown(); LanBeaverItem.Shutdown(); LanBeaverSave.Shutdown(); LanNotify.Shutdown(); LanSkinPicker.Shutdown(); LanBossArena.Shutdown(); LanDowned.Shutdown(); _identity?.Dispose(); }

        // 0.4.0-0.4.1 kept the relay address, fingerprint and key as plain text in
        // [Relay] Server/Fingerprint/AccessKey. A genuinely custom server moves to the
        // Custom* entries; the built-in values are dropped. The old keys are deleted.
        private void MigrateLegacyRelaySettings()
        {
            var server = Config.Bind("Relay", "Server", "", "Obsolete; removed on start");
            var fingerprint = Config.Bind("Relay", "Fingerprint", "", "Obsolete; removed on start");
            var key = Config.Bind("Relay", "AccessKey", "", "Obsolete; removed on start");
            try
            {
                string text = (server.Value ?? "").Trim();
                int colon = text.LastIndexOf(':');
                IPAddress address;
                bool builtIn = text.Length == 0 || IPAddress.TryParse(colon > 0 ? text.Substring(0, colon) : text, out address) && RelayDefaults.Available && address.Equals(RelayDefaults.Server);
                if (!builtIn && (_customServerEntry.Value ?? "").Trim().Length == 0)
                {
                    _customServerEntry.Value = text;
                    _customFingerprintEntry.Value = (fingerprint.Value ?? "").Trim();
                    _customKeyEntry.Value = key.Value ?? "";
                    Logger.LogInfo("Custom relay settings moved to [Relay] CustomServer/CustomFingerprint/CustomAccessKey");
                }
            }
            finally
            {
                Config.Remove(server.Definition); Config.Remove(fingerprint.Definition); Config.Remove(key.Definition);
                Config.Save();
            }
        }

        private bool CustomRelayConfigured { get { return (_customServerEntry.Value ?? "").Trim().Length != 0; } }

        private void Update()
        {
            LanChat.RestoreControl();
            UpdateLanInput();
            LanSkinPicker.Tick();
            // Every frame, with or without a session: a held arena trap settles after one ends.
            BossArenaTick();
            // 1.2.0: quests are the host's while a session is attached.
            QuestsTick();
            // 1.2.1: what the guest carries follows its avatar; a guest's robot dog follows the guest.
            TakablesTick();
            // 1.2.2: sleeping together, explosions for the guest, the partner on the map.
            CoopTick();
            // 1.1.9: a downed player, or the partner lying downed next to this one.
            LanDowned.Tick();
            // Keep the disposable client's world out of the player's solo saves,
            // including after disconnection. A restart restores normal save paths.
            if (LanSaveIsolation.Active && !LanSaveIsolation.Enforce())
                ClientTravelHooks.Fail("SaveIsolation");
            ClientTravelHooks.Tick();
            if (_roomBrowser != null && _roomBrowser.Finished && !_roomSearchReported)
            {
                _roomSearchReported = true;
                string error = _roomBrowser.SecurityFailure;
                Logger.LogInfo("LAN room search finished: " + _roomBrowser.Rooms.Length + (error == null ? "" : "; " + error));
            }
            _travel?.Hold();
            // Never attach host state to a solo world started while pairing or
            // authentication was pending, including before a DTLS socket exists.
            if ((_pairing != null || _network != null) && !_hosting && _clientSavePath == null &&
                GlobalManager.global != null && GlobalManager.global.controlledChar != null)
            {
                StopNetwork(); _status = "Подключение отменено: уже начата одиночная игра"; return;
            }
            MenuWhenReady(); // 1.4.5: a guest whose host left goes to the main menu
            PollPairing();
            NotifyApproval();
            if (_network == null) return;
            if(_network.SecurityFailure!=null) {
                string reason=_network.SecurityFailure;
                StopNetwork();_status=reason+"; связь закрыта. Нужны новый код и разрешение хоста";Logger.LogWarning(_status);return;
            }
            if (!_hosting && ClientTravelHooks.LoadFailure != null)
            {
                _trainSync?.Freeze(); _clock.Freeze(); StopSocketOnly();
                _connected = false; _peer = null; HideAvatar();
                _status = "Ошибка загрузки клиента. Перезапустите игру; сохранения изолированы";
                Logger.LogError("Client loader failed: " + ClientTravelHooks.LoadFailure);
                return;
            }
            try { _network.Tick(); }
            catch (Exception ex) { NetworkWarning(ex); }
            if (_connected && _network.Session != _session)
            {
                if (!_hosting && _lostAt < 0f) _lostAt = Time.unscaledTime;
                _connected = false;
                _zombies?.Freeze();
                _trainSync?.Freeze(); _clock.Freeze(); _poses.Clear(); HideAvatar();
                if (_hosting) _peer = null;
                _status = _hosting ? "Ожидание защищённого подключения" : "Повторное защищённое подключение";
            }
            if (_hosting) ObserveHostWorld();
            ReceivePackets();
            if(_peerWorkLimits.Failure!=null) {
                string reason=_peerWorkLimits.Failure;
                StopNetwork();_status=reason+"; соединение закрыто";Logger.LogWarning(_status);return;
            }
            float now = Time.unscaledTime;
            if (_connected && _peer != null && now - _lastReceive > 30f)
                PeerLost(_hosting ? "Связь с другом потеряна" : "Связь с хостом потеряна; повторное подключение");
            // 1.4.5: a guest waits for its host three minutes, then leaves the frozen world.
            if (!_hosting && !_connected && _lostAt >= 0f && now - _lostAt > GuestWaitForHost) { HostLeft("Хост не вернулся за 3 минуты"); return; }
            if (_connected)
            {
                var me = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
                RemoteEquipment.ObserveDash(me); RemoteEquipment.ObserveMotion(me);
            }
            if (_connected && now >= _nextStateAt)
            {
                SendState();
                _nextStateAt = Mathf.Max(_nextStateAt + StateInterval, now - StateInterval);
            }
            if (_connected && now - _lastSend >= SendInterval)
            {
                if (_hosting && _hostWorldReady && now >= _nextWorldSend) SendWorld();
                Send(PacketKind.Heartbeat);
                _lastSend = now;
            }
            if (_connected && !_hosting) UpdateClientWorld();
            if (_connected && _hosting) _guestHost?.Tick(Time.unscaledTime);
            // Friends: a new friend's belongings go onto the ledger as they are (configurable).
            if (_connected && _hosting && _guestHost != null && _guestHost.PendingKit != null && !_askBelongingsEntry.Value) _guestHost.AcceptKit(true);
            if (_connected && !_hosting && _guestClient != null)
                _guestClient.Tick(Time.unscaledTime, _clientPlacedNearHost && !_travel.Active && LocalWorldReady() && LanWorldItems.Dropped.Count == 0 && (_storageClient == null || !_storageClient.Busy),
                    () => LanGuestCharacter.Capture(new[] { _storageClient == null ? 0u : _storageClient.AppliedTx, _zombies == null ? 0u : _zombies.Combat.AppliedState }));
            if (_connected && !_hosting && _guestClient != null && _guestClient.CorrectionPending && _clientPlacedNearHost && !_travel.Active &&
                LocalWorldReady() && !LanDowned.Down && !LanSleep.Waiting && (_storageClient == null || !_storageClient.Busy))
            {
                // The host's ledger replaces belongings it could not account for.
                LanStorageGame.CloseWindow(_storageClient);
                RestoreGuestCharacter(_guestClient.TakePending());
                Logger.LogInfo("Guest belongings corrected to the host's ledger");
            }
            if (_connected && _hosting) _storageHost?.Tick(Time.unscaledTime);
            if (_hosting && _connected && !_connectionPanelClosed && _guestHost != null && _guestHost.Confirmed && GuestStoragePosition() != null) CloseConnectedPanel();
            if (!_hosting) UpdateGuestStorage();
            if (!_hosting) UpdateGuestDeath();
            if (_connected && _trainSync != null)
                _trainSync.Tick(_hosting, TrainWorldAvailable() && (_hosting ? _hostWorldReady : _clientLoadEpoch == _worldEpoch));
            if (_connected && _zombies != null)
            {
                bool ready = TrainWorldAvailable() && (_hosting ? _hostWorldReady :
                    _clientWorld != null && _clientLoadEpoch == _worldEpoch &&
                    GlobalSceneManager.global.CurrentSceneName == _clientWorld.Scene);
                string scene = _hosting ? _hostWorldScene : _clientWorld?.Scene;
                _zombies.RemoteDown = _remoteDowned || _remoteDead;
                // 1.4.13: how the guest moves and its stealth level, for the host's zombies.
                _zombies.RemoteCrouch = _remoteCrouch; _zombies.RemoteSpeed = _remoteSpeed;
                _zombies.RemoteRun = (_remoteMotion & LanProtocol.MotionRun) != 0; _zombies.RemoteAir = (_remoteMotion & LanProtocol.MotionAir) != 0;
                var stealthBook = _hosting ? GuestLedger() : null; _zombies.RemoteStealth = stealthBook == null ? 1f : stealthBook.StealthFactor;
                _zombies.Tick(scene, _worldEpoch, ready, RemoteWorldPosition(),
                    ready && _haveRemoteState && Time.unscaledTime - _remoteStateAt < 2f && _remoteScene == scene,
                    _remoteEquippedItem, _remoteYaw, _hosting || _clientPlacedNearHost);
            }
            if (!_connected || _doors == null) { LanWorldDoors.GuestToggle = null; LanWorldDoors.GuestUnlock = null; LanWorldItems.GuestActive = false; LanTrainBuild.GuestAttached = false; LanTrainControl.GuestAttached = false; }
            else
            {
                bool ready = TrainWorldAvailable() && (_hosting ? _hostWorldReady :
                    _clientWorld != null && _clientLoadEpoch == _worldEpoch && _clientPlacedNearHost &&
                    GlobalSceneManager.global.CurrentSceneName == _clientWorld.Scene);
                _doors.Tick(_hosting ? _hostWorldScene : _clientWorld?.Scene, _worldEpoch, ready, _hosting ? GuestStoragePosition() : null);
                _sceneStates?.Tick(_hosting ? _hostWorldScene : _clientWorld?.Scene, _worldEpoch, ready, _hosting ? GuestStoragePosition() : null);
                _worldItems?.Tick(_hosting ? _hostWorldScene : _clientWorld?.Scene, _worldEpoch, ready, _hosting ? GuestStoragePosition() : null);
                var doors = _doors;
                LanWorldDoors.GuestToggle = !_hosting && ready ? (Action<DoorController>)(door => doors.RequestToggle(door)) : null;
                LanWorldDoors.GuestUnlock = !_hosting && ready ? (Action<DoorController, int>)((door, key) => doors.RequestUnlock(door, key)) : null;
                _quests?.Tick(_hosting ? _hostWorldScene : _clientWorld?.Scene, _worldEpoch, ready);
                LanWorldItems.GuestActive = !_hosting && ready;
                LanTrainBuild.GuestAttached = !_hosting && ready;
                LanTrainControl.GuestAttached = !_hosting && ready;
                _marks?.Tick(_hosting ? _hostWorldScene : _clientWorld?.Scene, _worldEpoch, ready && !_panelOpen);
            }
            _chat?.Tick(_connected && !_panelOpen);
            FinishNativeLoad();
            UpdateAvatar();
            LogLocalEquipment();
        }

        private void LateUpdate()
        {
            BookDroppedItems();
            if (_connected)
            {
                if (!_hosting && _trainSync != null) _trainSync.Render();
                if (!_hosting) TrainLayout.ShowBlueprints(LanTrainBuild.BlueprintsShownNow()); // 1.4.11
                if (!_hosting) _clock.Render(_clientPlacedNearHost && _clientWorld != null &&
                    GlobalSceneManager.global != null && !ClientTravelHooks.Loading &&
                    !GlobalSceneManager.global.SceneCurrentlyLoading && GlobalSceneManager.global.CurrentSceneName == _clientWorld.Scene);
                // Car-relative avatars must follow the same final train pose.
                if (_avatar != null) UpdateRemoteAvatarPose();
            }
        }

        private void OnGUI()
        {
            var inputEvent = Event.current;
            if (inputEvent.type == EventType.KeyDown && inputEvent.keyCode == KeyCode.F8)
            {
                _chat?.Close(); ToggleLanPanel(); inputEvent.Use();
            }
            if (_connected && _chat != null && !_panelOpen)
            {
                _chat.KeyEvent(inputEvent);
                _chat.Draw();
            }
            LanTakables.DrawWindow();
            LanRobotWindow.Draw();
            LanSleep.Draw();
            if (_connected && _marks != null && !_panelOpen)
            {
                _marks.KeyEvent(inputEvent);
                _marks.Draw(_avatar != null && _avatar.activeSelf ? RemoteWorldPosition() : (Vector3?)null);
            }
            // 1.0.2: further from the right edge, and every line wraps to the panel's width (long
            // labels and the tab bar used to run past the right edge of the screen).
            float width = Math.Min(420f, Screen.width - 24f);
            float x = Math.Max(12f, Screen.width - width - Math.Max(24f, Screen.width * .08f));
            // Labels, check boxes and text fields all wrap: one long unwrapped check box widened
            // the whole column past the panel (1.0.3).
            bool labelWrap = GUI.skin.label.wordWrap, toggleWrap = GUI.skin.toggle.wordWrap, fieldWrap = GUI.skin.textField.wordWrap, buttonWrap = GUI.skin.button.wordWrap;
            GUI.skin.label.wordWrap = true; GUI.skin.toggle.wordWrap = true; GUI.skin.textField.wordWrap = true; GUI.skin.button.wordWrap = true;
            float height = _panelOpen ? Math.Min(680f, Screen.height - 24f) : 44f;
            GUILayout.BeginArea(new Rect(x, 12f, width, height), GUI.skin.box);
            if (GUILayout.Button(_panelOpen ? "LAN ▲ · F8" : "LAN: играть вместе ▼ · F8")) ToggleLanPanel();
            if (_panelOpen)
            {
                // 1.0.4: a plain scroll view around an area of fixed width. In a layout scroll view the
                // widest control that cannot wrap set the width of every line, so lines ran off the panel.
                var view = new Rect(6f, 36f, width - 12f, height - 42f);
                float contentWidth = view.width - 18f, contentHeight = Math.Max(_panelContentHeight, view.height);
                _panelScroll = GUI.BeginScrollView(view, _panelScroll, new Rect(0f, 0f, contentWidth, contentHeight));
                GUILayout.BeginArea(new Rect(0f, 0f, contentWidth, contentHeight));
                GUILayout.Label("LAN " + PluginVersion + (_relayMode ? " · Через интернет-сервер" : " · Одна локальная сеть"));
                bool wasEnabled = GUI.enabled;
                GUI.enabled = wasEnabled && _isolated == null; // switch only while idle
                int tab = GUILayout.Toolbar(_relayMode ? 1 : 0, new[] { "Локальная сеть", "Через интернет-сервер" });
                GUI.enabled = wasEnabled;
                if ((tab == 1) != _relayMode) { _relayMode = tab == 1; _useRelayEntry.Value = _relayMode; _status = "Не подключено"; }
                if (_relayMode) DrawRelayPanel(); else DrawLanPanel();
                if (_trainSync != null && !_hosting) { GUILayout.Label(_trainSync.Status); GUILayout.Label(_trainSync.StairHint); }
                GUILayout.Space(2f);
                if (Event.current.type == EventType.Repaint) _panelContentHeight = GUILayoutUtility.GetLastRect().yMax + 6f;
                GUILayout.EndArea();
                GUI.EndScrollView();
            }
            GUILayout.EndArea();
            GUI.skin.label.wordWrap = labelWrap; GUI.skin.toggle.wordWrap = toggleWrap; GUI.skin.textField.wordWrap = fieldWrap; GUI.skin.button.wordWrap = buttonWrap;
            if (!_hosting) LanStorageGame.DrawNotice();
            if (!_hosting) LanGuestDeath.Draw();
            if (!_hosting && _connected && LanTrainControl.HostDead && !LanGuestDeath.Dead)
                GUI.Box(new Rect(Screen.width / 2f - 260f, 70f, 520f, 46f), "Хост погиб. Когда он загрузит сохранение, вы перенесётесь к нему.");
            if (_hosting && _connected && _guestHost != null && _guestHost.PendingKit != null && !_panelOpen)
                GUI.Box(new Rect(x, 124f, width, 56f), "Новый друг пришёл с вещами. Откройте LAN (F8) и решите, принять ли их.");
            if (_pairing != null && _pairing.PendingApproval && !_panelOpen)
            {
                GUI.Box(new Rect(x, 64f, width, 56f), "Друг подтвердил код. Откройте LAN (F8), чтобы разрешить.");
            }
            DrawPartnerHealth();
            LanDowned.Draw(LanSkinPicker.PartnerLabel(_hosting));
            LanNotify.Draw();
            LanSkinPicker.Draw();
        }

        // 1.0.8: the host hears and sees a friend waiting for permission, and is reminded every
        // 20 seconds while the LAN panel stays closed.
        private void NotifyApproval()
        {
            if (_pairing == null || !_pairing.PendingApproval) { _approvalNotified = false; _approvalRemindAt = 0f; return; }
            if (Time.unscaledTime < _approvalRemindAt || _approvalNotified && _panelOpen) return;
            LanNotify.Alert("Друг просит подключиться: откройте LAN (F8) и разрешите");
            _approvalNotified = true; _approvalRemindAt = Time.unscaledTime + 20f;
        }

        // 1.0.8: the partner's health; 1.1.6: only while the player looks at the partner from close
        // by, above the partner's head, in the game's own numbers (current / maximum), dimmed while
        // its states are late.
        internal const float HealthLookRange = 12f;
        private GUIStyle _healthStyle, _healthShadow;
        private void DrawPartnerHealth()
        {
            if (!_partnerHealthEntry.Value || !_connected || !_haveRemoteState || Event.current.type != EventType.Repaint) return;
            if (_avatar == null || !_avatar.activeInHierarchy || _remoteHealthMax == 0 && _remoteHealth == LanProtocol.NoHealth) return;
            var manager = GlobalManager.global;
            var player = manager == null ? null : manager.controlledChar;
            var camera = manager == null || manager.MainCamera == null ? Camera.main : manager.MainCamera;
            var input = Zompiercer.Inputs.InputController.Global;
            if (player == null || camera == null || input == null || (int)input.CurrentState != 1) return;
            Vector3 eye = camera.transform.position, root = _avatar.transform.position - Vector3.up * _avatarYOffset;
            Vector3 chest = root + Vector3.up * 1.2f, head = root + Vector3.up * 2.05f;
            float distance = Vector3.Distance(eye, chest);
            if (distance > HealthLookRange || distance < .3f) return;
            // On the partner: within its half width (and a little more) of the view's centre.
            if (Vector3.Angle(camera.transform.forward, chest - eye) > Mathf.Atan2(.55f, distance) * Mathf.Rad2Deg + 2f) return;
            foreach (var hit in Physics.RaycastAll(eye, (chest - eye) / distance, distance - .4f, ~0, QueryTriggerInteraction.Ignore))
                if (hit.collider != null && !hit.collider.transform.IsChildOf(player.transform)) return; // behind a wall
            var screen = camera.WorldToScreenPoint(head);
            if (screen.z <= 0f) return;
            float share = _remoteHealthMax > 0 ? (float)_remoteHealthNow / _remoteHealthMax : _remoteHealth / 100f;
            float alpha = Time.unscaledTime - _remoteStateAt > 3f ? .5f : 1f;
            if (_healthStyle == null)
            {
                _healthStyle = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = false, fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _healthStyle.normal.textColor = Color.white;
                _healthShadow = new GUIStyle(_healthStyle); _healthShadow.normal.textColor = Color.black;
            }
            var frame = new Rect(screen.x - 85f, Screen.height - screen.y - 22f, 170f, 22f);
            var fill = new Rect(frame.x + 3f, frame.y + 3f, (frame.width - 6f) * Mathf.Clamp01(share), frame.height - 6f);
            var previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, .6f * alpha);
            GUI.DrawTexture(frame, Texture2D.whiteTexture);
            GUI.color = share > .5f ? new Color(.35f, .8f, .35f, alpha) : share > .25f ? new Color(.95f, .75f, .2f, alpha) : new Color(.9f, .25f, .2f, alpha);
            GUI.DrawTexture(fill, Texture2D.whiteTexture);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            string text = LanSkinPicker.PartnerLabel(_hosting) + " · " + (_remoteDowned ? "ранен" : _remoteHealthMax > 0 ? _remoteHealthNow + " / " + _remoteHealthMax : _remoteHealth + "%");
            GUI.Label(new Rect(frame.x + 1f, frame.y + 1f, frame.width, frame.height), text, _healthShadow);
            GUI.Label(frame, text, _healthStyle);
            GUI.color = previous;
        }

        // 1.1.9: downed or dead, the helmet and weapon flashlights, a reload of the weapon in hand.
        private static byte LocalPoseExtras(ZombieFighterController player)
        {
            int pose = 0;
            try
            {
                if (LanDowned.Down) pose |= LanProtocol.PoseDowned;
                else if (LanGuestDeath.Dead || LanGuestDeath.LocalDead()) pose |= LanProtocol.PoseDead;
                var weapons = player.zombieFighterFireArmWeapon;
                if (weapons != null)
                {
                    if (weapons.HelmetFlashlightEnabled) pose |= LanProtocol.PoseHelmetLight;
                    var gun = weapons.GetSelectedGun();
                    if (gun != null)
                    {
                        if (gun.reloading) pose |= LanProtocol.PoseReload;
                        var lamp = weapons.FlashlightEnabled ? gun.GetComponentInChildren<WeaponFlashlight>(true) : null;
                        if (lamp != null && lamp.flashlight != null && lamp.flashlight.activeSelf) pose |= LanProtocol.PoseGunLight;
                    }
                }
            }
            catch (Exception) { }
            return (byte)pose;
        }

        // Aiming down the sights (the game's own aim state of the weapon in hand).
        private static bool LocalAiming(ZombieFighterController player)
        {
            var weapons = player == null ? null : player.weaponAnimator;
            return weapons != null && weapons.InputAim && RemoteEquipment.CaptureLocalItemId() >= 0;
        }

        // Where the player looks up (+) or down, from the view the shots leave from.
        private static float LocalPitch(ZombieFighterController player)
        {
            var rays = player == null ? null : player.zombieFighterRays;
            var view = rays == null ? null : rays.cameraPoint;
            if (view == null) return 0f;
            float pitch = Mathf.Asin(Mathf.Clamp(view.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            return float.IsNaN(pitch) ? 0f : Mathf.Clamp(pitch, -LanProtocol.MaxPitch, LanProtocol.MaxPitch);
        }

        // 1.1.6: health points as the game's own bar shows them (SetCurrentValue/SetMaxValue).
        private static void LocalHealthPoints(out ushort now, out ushort max)
        {
            now = max = 0;
            try
            {
                var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
                var bar = player == null ? null : player.zombieFighterIndicatorsBar;
                if (bar == null) return;
                float top = bar.MaxHealth, current = bar.deathRegister ? 0f : bar.currentHealth;
                if (float.IsNaN(top) || float.IsInfinity(top) || float.IsNaN(current) || float.IsInfinity(current) || top < 1f) return;
                max = (ushort)Mathf.Clamp(Mathf.RoundToInt(top), 1, 65535);
                now = (ushort)Mathf.Clamp(Mathf.RoundToInt(current), 0, max);
            }
            catch (Exception) { now = max = 0; }
        }

        private static byte LocalHealth()
        {
            try
            {
                var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
                var bar = player == null ? null : player.zombieFighterIndicatorsBar;
                if (bar == null) return LanProtocol.NoHealth;
                if (bar.deathRegister || bar.currentHealth <= 0f) return 0;
                float max = bar.MaxHealth, share = bar.currentHealth / max;
                if (!(max > 0f) || float.IsNaN(share) || float.IsInfinity(share)) return LanProtocol.NoHealth;
                return (byte)Mathf.Clamp(Mathf.RoundToInt(share * 100f), 1, 100); // alive: never 0
            }
            catch (Exception) { return LanProtocol.NoHealth; }
        }

        // 1.0.5: the panel has a framed area for creating a room and another for joining one,
        // each with its own fields and button; the shared status and "disconnect" come below.
        private GUIStyle _sectionTitle;
        private void BeginSection(string title)
        {
            if (_sectionTitle == null) _sectionTitle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            _sectionTitle.wordWrap = true;
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(title, _sectionTitle);
        }
        private static void EndSection() { GUILayout.EndVertical(); GUILayout.Space(6); }

        private void DrawSessionFooter()
        {
            GUILayout.Label("Состояние: " + _status);
            if (GUILayout.Button("Отключиться / закрыть комнату")) StopNetwork();
            DrawCombat();
            DrawGuestProfile();
        }

        private void DrawLanPanel()
        {
            BeginSection("1. Создать свою комнату (вы — хост)");
            GUILayout.Label("Имя вашей комнаты:");
            _roomName = GUILayout.TextField(_roomName, 20);
            if (GUILayout.Button("Создать комнату")) StartHosting();
            if (_hosting)
            {
                if (_pairing != null && _pairing.PendingApproval)
                {
                    GUILayout.Label("Код подтверждён с адреса " + _pairing.PendingPeer + ". Разрешить подключение?");
                    GUILayout.Label("IP — адрес соединения, а не подтверждение личности. Сверьте запрос с другом.");
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Разрешить")) _pairing.Approve();
                    if (GUILayout.Button("Отклонить")) _pairing.Reject();
                    GUILayout.EndHorizontal();
                }
                else if (_network == null && (_pairing == null || _pairing.Finished))
                {
                    if (GUILayout.Button("Новый код")) StartHosting();
                }
                else if (_pairing != null && _pairing.Code.Length != 0)
                {
                    GUILayout.Label("Код для друга: " + _pairing.Code + " · осталось " + _pairing.SecondsLeft + " с");
                    if (GUILayout.Button("Скопировать код")) GUIUtility.systemCopyBuffer = _pairing.Code;
                }
            }
            else GUILayout.Label("После создания здесь появится код из шести цифр — передайте его другу.");
            GUILayout.Label("Оба компьютера должны быть в одной локальной сети.");
            EndSection();

            BeginSection("2. Войти в комнату друга");
            if (GUILayout.Button(_roomBrowser != null && !_roomBrowser.Finished ? "Поиск…" : "Найти комнаты в сети")) RefreshRooms();
            if (_roomBrowser != null)
            {
                GUILayout.Label(_roomBrowser.Status);
                if (_roomBrowser.Finished && _roomBrowser.Rooms.Length == 0)
                    GUILayout.Label("Для разрешения LAN в Windows запустите «Настроить LAN.cmd» из папки игры на обоих ПК.");
                if (_roomBrowser.Rooms.Length != 0)
                {
                    _roomScroll = GUILayout.BeginScrollView(_roomScroll, GUILayout.Height(Math.Min(155f, 30f * _roomBrowser.Rooms.Length + 8f)));
                    foreach (var room in _roomBrowser.Rooms)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(room.Name + " · " + room.Address + (room.Busy ? " · занято" : room.PinActive ? " · код активен" : " · нужен новый код"));
                        bool previousEnabled = GUI.enabled;
                        GUI.enabled = previousEnabled && room.PinActive && !_hosting && _network == null && _pairing == null;
                        if (GUILayout.Button("Войти", GUILayout.Width(62f))) StartJoining(room.Address);
                        GUI.enabled = previousEnabled; GUILayout.EndHorizontal();
                    }
                    GUILayout.EndScrollView();
                }
            }
            GUILayout.Label("Шесть цифр от хоста:");
            _joinCode = GUILayout.TextField(_joinCode, 6);
            GUILayout.Label("Резервный IPv4 адрес хоста (необязательно):");
            _manualAddress = GUILayout.TextField(_manualAddress, 15);
            if (GUILayout.Button("Подключиться")) StartJoining();
            GUILayout.Label("Код действует " + LanLocalText.PinSeconds + " секунд, потом хост разрешает подключение. Список комнат не подтверждает личность хоста: всё равно нужен его код.");
            EndSection();

            DrawSessionFooter();
        }

        private void DrawRelayPanel()
        {
            bool wasEnabled = GUI.enabled;
            bool idle = _isolated == null; // settings are fixed for a running session
            GUILayout.Label(CustomRelayConfigured ? "Сервер: свой, из конфига BepInEx ([Relay] CustomServer)" : RelayDefaults.Available ? "Сервер: встроенный в эту сборку мода" :
                "В этой сборке нет интернет-сервера: играйте по локальной сети или скачайте готовую сборку мода (GitHub, Releases)");

            BeginSection("1. Создать свою комнату (вы — хост)");
            GUI.enabled = wasEnabled && idle;
            GUILayout.Label("Название комнаты (друг увидит его после проверки пароля):");
            _roomName = GUILayout.TextField(_roomName, 20);
            _relayPublic = GUILayout.Toggle(_relayPublic, " Показать комнату в общем списке (на сервере её увидят другие игроки)");
            GUILayout.Label(_relayPublic ? "Пароль комнаты (обязательно для общего списка; придумайте сами, от 8 символов):" :
                "Свой пароль комнаты (необязательно; пусто — случайный PIN):");
            _relayHostSecret = GUILayout.PasswordField(_relayHostSecret, '*', LanRelayText.MaxSecret);
            string warning = _relayPublic ? LanRelayText.PublicSecretProblem(_relayHostSecret) : LanRelayText.SecretWarning(_relayHostSecret);
            if (warning != null) GUILayout.Label((_relayPublic ? "" : "Внимание: ") + warning);
            if (_relayPublic) GUILayout.Label("В списке видно только название «" + LanRelayText.PublicName(_roomName) + "». Пароль на сервер не уходит; каждого игрока вы всё равно разрешаете сами.");
            GUI.enabled = wasEnabled;
            if (GUILayout.Button("Создать комнату")) StartHosting();
            if (_hosting)
            {
                if (_pairing != null && _pairing.PendingApproval)
                {
                    GUILayout.Label("Друг подтвердил пароль комнаты через сервер. Разрешить подключение?");
                    GUILayout.Label("Сервер не показывает адрес друга. Убедитесь, что пароль вводил именно он.");
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Разрешить")) _pairing.Approve();
                    if (GUILayout.Button("Отклонить")) _pairing.Reject();
                    GUILayout.EndHorizontal();
                }
                else if (_network == null && (_pairing == null || _pairing.Finished))
                {
                    if (GUILayout.Button("Новая комната")) StartHosting();
                }
                else if (_pairing != null && _pairing.InvitationOpen && _pairing.RelayRoom.Length != 0)
                {
                    string room = LanRelayText.FormatRoom(_pairing.RelayRoom);
                    bool custom = _pairing.CustomSecret;
                    if (_hostedPublic) GUILayout.Label("«" + LanRelayText.PublicName(_roomName) + "» в общем списке · код " + room + " · пароль задан вами");
                    else GUILayout.Label("«" + LanLocalText.CleanName(_roomName) + "» · код " + room + (custom ? " · пароль задан вами" : " · PIN " + _pairing.Code) +
                        " · осталось " + _pairing.SecondsLeft + " с");
                    if (GUILayout.Button("Скопировать для друга"))
                        GUIUtility.systemCopyBuffer = custom ? "Комната " + room + " (пароль сообщу отдельно)" : "Комната " + room + ", PIN " + _pairing.Code;
                }
                GUILayout.Label("Передайте другу код комнаты и PIN или пароль, например в мессенджере.");
            }
            else GUILayout.Label("После создания здесь появятся код комнаты и PIN — передайте их другу.");
            GUILayout.Label(_hostedPublic && _hosting ? "Пароль на сервер не передаётся; комната в списке около 28 минут, до 10 попыток входа." :
                "Пароль на сервер не передаётся; три попытки входа на комнату.");
            EndSection();

            BeginSection("2. Войти в комнату друга");
            bool listing = _relayRooms != null && !_relayRooms.Finished;
            GUI.enabled = wasEnabled && idle && !listing && Time.unscaledTime - _relayRoomsAt >= 3f;
            if (GUILayout.Button(listing ? "Загрузка списка…" : "Открытые комнаты на сервере")) RefreshRelayRooms();
            GUI.enabled = wasEnabled && idle;
            if (_relayRooms != null)
            {
                GUILayout.Label(_relayRooms.Status);
                var rooms = _relayRooms.Finished ? _relayRooms.Rooms : new LanRoomInfo[0];
                if (rooms.Length != 0)
                {
                    _relayRoomsScroll = GUILayout.BeginScrollView(_relayRoomsScroll, GUILayout.Height(Math.Min(155f, 30f * rooms.Length + 8f)));
                    foreach (var room in rooms)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(room.Name + " · 1/2 · по паролю");
                        if (GUILayout.Button("Выбрать", GUILayout.Width(80f)))
                        {
                            _relayRoomInput = LanRelayText.FormatRoom(room.RelayRoom);
                            _status = "Комната «" + room.Name + "» выбрана. Введите пароль, который сообщил хост, и нажмите «Подключиться»";
                        }
                        GUILayout.EndHorizontal();
                    }
                    GUILayout.EndScrollView();
                }
            }
            GUILayout.Label("Код комнаты от хоста (8 символов, например 7Q2M-XK4D):");
            _relayRoomInput = GUILayout.TextField(_relayRoomInput, 9);
            GUILayout.Label("PIN или пароль комнаты от хоста:");
            GUILayout.BeginHorizontal();
            _relayJoinSecret = _showJoinSecret ? GUILayout.TextField(_relayJoinSecret, LanRelayText.MaxSecret) :
                GUILayout.PasswordField(_relayJoinSecret, '*', LanRelayText.MaxSecret);
            _showJoinSecret = GUILayout.Toggle(_showJoinSecret, "показать", GUILayout.Width(80f));
            GUILayout.EndHorizontal();
            GUI.enabled = wasEnabled;
            if (GUILayout.Button("Подключиться")) StartJoining();
            GUILayout.Label("Хост создаёт комнату, сообщает её код и PIN или пароль, а потом разрешает подключение.");
            EndSection();

            DrawSessionFooter();
        }

        private void DrawCombat()
        {
            if (!_connected || _zombies == null) return;
            GUILayout.Label(_zombies.Combat.Description);
            if (_hosting && _zombies.Combat.Approved && GUILayout.Button("Запретить другу бой до новой комнаты")) _zombies.Combat.DenyOrRevoke();
        }

        // Built-in relay of this build, or the advanced Custom* config entries.
        // Returns null with _status set on error. Nothing here is written back to config.
        private LanRelayOptions RelayOptions(bool join)
        {
            string room = "";
            if (join && (room = LanRelayText.NormalizeRoom(_relayRoomInput)) == null) { _status = "Введите код комнаты сервера: 8 символов, например 7Q2M-XK4D"; return null; }
            if (!CustomRelayConfigured)
            {
                if (RelayDefaults.Available) return RelayDefaults.Create(room);
                _status = "В этой сборке нет интернет-сервера: играйте по локальной сети или скачайте готовую сборку мода (GitHub, Releases)"; return null;
            }
            string text = _customServerEntry.Value.Trim();
            int port = LanRelayText.DefaultPort, colon = text.LastIndexOf(':');
            if (colon > 0 && (!int.TryParse(text.Substring(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535))
            { _status = "Свой сервер в конфиге: неверный порт"; return null; }
            IPAddress server;
            if (!IPAddress.TryParse(colon > 0 ? text.Substring(0, colon) : text, out server) || !LanRelayText.ValidServer(server))
            { _status = "Свой сервер в конфиге: нужен публичный IPv4-адрес"; return null; }
            byte[] fingerprint = LanRelayText.ParseFingerprint(_customFingerprintEntry.Value);
            if (fingerprint == null) { _status = "Свой сервер в конфиге: укажите CustomFingerprint (64 шестнадцатеричных символа)"; return null; }
            string key = _customKeyEntry.Value ?? "";
            if (System.Text.Encoding.UTF8.GetByteCount(key) > 64) { _status = "Свой сервер в конфиге: ключ длиннее 64 байт"; return null; }
            return new LanRelayOptions { Server = server, Port = port, Fingerprint = fingerprint, AccessKey = key, Room = room };
        }

        private static bool GameMenusVisible()
        {
            var manager = GlobalManager.global;
            var menus = manager == null ? null : manager.canvas;
            if (menus == null) menus = Object.FindObjectOfType<Zompiercer.GUI.GUIManager>();
            return Time.timeScale == 0f || (menus != null && ((menus.mainGameMenu != null && menus.mainGameMenu.activeInHierarchy) ||
                (menus.startGameMenu != null && menus.startGameMenu.activeInHierarchy) ||
                (menus.inGameMenu != null && menus.inGameMenu.activeInHierarchy)));
        }
        private void ToggleLanPanel()
        {
            _panelOpen = !_panelOpen;
            if (_panelOpen && _roomBrowser == null && !_relayMode) RefreshRooms();
            UpdateLanInput();
        }
        private void CloseConnectedPanel()
        {
            _connectionPanelClosed = true;
            _panelOpen = false;
            ReleaseLanInput();
        }
        private void UpdateLanInput()
        {
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var scenes = GlobalSceneManager.global;
            var saves = Zompiercer.SaveLoad.SaveLoadCore.global;
            var input = Zompiercer.Inputs.InputController.Global;
            // The host's storages use the game's own windows, which manage the cursor themselves.
            // Never capture the loader's disabled control state as the state to restore.
            if (!_panelOpen || GameMenusVisible() || player == null ||
                scenes != null && scenes.SceneCurrentlyLoading || saves != null && saves.LoadingNow ||
                input == null || (int)input.CurrentState != 1 || ClientTravelHooks.Loading ||
                ClientTravelHooks.LoadFailure != null || _travel != null && _travel.Active ||
                _lanHeldPlayer != player && !player.controlEnabled)
            { ReleaseLanInput(); return; }
            if (_lanHeldPlayer != player)
            {
                ReleaseLanInput(); _lanHeldPlayer = player;
                _lanPreviousControl = player.controlEnabled; _lanPreviousCursor = Cursor.visible; _lanPreviousLock = Cursor.lockState;
            }
            player.controlEnabled = false; Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
        private void ReleaseLanInput()
        {
            var player = _lanHeldPlayer;
            if (player == null || GlobalManager.global == null || GlobalManager.global.controlledChar != player)
            { _lanHeldPlayer = null; return; }
            if (GameMenusVisible()) return;
            bool gameplay = Zompiercer.Inputs.InputController.Global != null && (int)Zompiercer.Inputs.InputController.Global.CurrentState == 1;
            var saves = Zompiercer.SaveLoad.SaveLoadCore.global;
            if (gameplay && (GlobalSceneManager.global == null || !GlobalSceneManager.global.SceneCurrentlyLoading) &&
                (saves == null || !saves.LoadingNow) && !ClientTravelHooks.Loading && ClientTravelHooks.LoadFailure == null)
            { _lanHeldPlayer = null; player.controlEnabled = _lanPreviousControl; Cursor.lockState = _lanPreviousLock; Cursor.visible = _lanPreviousCursor; }
        }
        // 1.1.6: the game ends its own load of a saved world with CharGuiOn and CursorLock
        // (SaveLoadCore.LoadWorldFromDataCoroutine); a guest's world comes through the scene loader,
        // which skips both for a location other than the first. Without them the quick-access belt
        // ignored its keys until Escape -> "back to the game" did the same. Done once the player
        // stands in the host's world with no window, menu or LAN panel open.
        private void FinishNativeLoad()
        {
            if (!_nativeHudPending) return;
            if (!_connected || _hosting) { _nativeHudPending = false; return; }
            var manager = GlobalManager.global;
            var player = manager == null ? null : manager.controlledChar;
            var gui = manager == null ? null : manager.canvas;
            var input = Zompiercer.Inputs.InputController.Global;
            if (!_clientPlacedNearHost || player == null || gui == null || input == null || (int)input.CurrentState != 1 || _panelOpen ||
                _lanHeldPlayer != null || LanGuestDeath.Dead || GameMenusVisible() || GameWindowOpen() ||
                GlobalSceneManager.global == null || GlobalSceneManager.global.SceneCurrentlyLoading || ClientTravelHooks.Loading) return;
            _nativeHudPending = false;
            try { gui.CharGuiOn(); gui.CursorLock(); Logger.LogInfo("Guest world ready: character HUD on, cursor locked"); }
            catch (Exception ex) { Logger.LogWarning("Guest HUD not finished: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void RefreshRooms()
        {
            if (_roomBrowser != null && !_roomBrowser.Finished) return;
            _roomBrowser?.Dispose();
            try { _roomSearchReported = false; _roomBrowser = LanIsolatedSession.Browse(); }
            catch (Exception ex) { _roomBrowser = null; _status = "Поиск комнат недоступен; можно использовать IP"; Logger.LogWarning(ex.GetType().Name); }
        }

        private void StartHosting()
        {
            if (LanSaveIsolation.Active) { _status = "После игры клиентом перезапустите игру для создания хоста"; return; }
            if (_network != null && _connected) { _status = "Комната уже подключена; сначала отключите текущего игрока"; return; }
            if (_pairing != null && _pairing.PendingApproval) { _status = "Сначала разрешите или отклоните запрос друга"; return; }
            if (ClientTravelHooks.Loading || (GlobalSceneManager.global != null && GlobalSceneManager.global.SceneCurrentlyLoading))
            { _status = "Дождитесь окончания загрузки мира"; return; }
            LanRelayOptions relay = null;
            string secret = "";
            if (_relayMode)
            {
                // Empty: random six-digit PIN. Otherwise the host's own password (6..64 characters).
                secret = LanRelayText.NormalizeSecret(_relayHostSecret);
                if (secret.Length != 0 && !LanRelayText.ValidSecret(secret)) { _status = "Пароль комнаты: от 6 до 64 символов, без управляющих символов"; return; }
                string problem = _relayPublic ? LanRelayText.PublicSecretProblem(secret) : null;
                if (problem != null) { _status = problem; return; }
                if ((relay = RelayOptions(false)) == null) return;
                if (_relayPublic) relay.PublicName = LanRelayText.PublicName(_roomName);
            }
            try
            {
                LanInventoryGuard.BackupSavesBeforeSession(Logger);
                LanInventoryGuard.CaptureCheckpoint("before-host-session", Logger);
                StopNetwork();
                _hosting = true;
                _hostedPublic = relay != null && relay.PublicName.Length != 0;
                _isolated = relay != null ? LanIsolatedSession.RelayHost(relay, NetworkGameVersion, LanLocalText.CleanName(_roomName), secret)
                    : LanIsolatedSession.Host(LanLocalText.CleanName(_roomName), NetworkGameVersion);
                _pairing = _isolated;
                _status = _pairing.Status;
                Logger.LogInfo(relay != null ? "Relay invitation requested from the " + (CustomRelayConfigured ? "custom" : "built-in") + " relay; room code and secret omitted from logs"
                    : "LAN invitation opened for " + LanLocalText.PinSeconds + " seconds; PIN omitted from logs");
            }
            catch (Exception ex)
            {
                _status = "Не удалось создать LAN-сессию: " + ex.Message;
                Logger.LogError(ex);
                StopSocketOnly();
            }
        }

        // The relay's public room list (0.13.0), read by a short-lived worker of its own.
        private void RefreshRelayRooms()
        {
            if (_relayRooms != null && !_relayRooms.Finished) return;
            _relayRooms?.Dispose(); _relayRooms = null;
            _relayRoomsAt = Time.unscaledTime;
            var relay = RelayOptions(false);
            if (relay == null) return;
            try { _relayRooms = LanIsolatedSession.RelayBrowse(relay); }
            catch (Exception ex) { _status = "Список комнат недоступен"; Logger.LogWarning("Relay room list: " + ex.GetType().Name); }
        }

        private void StartJoining(IPAddress selectedAddress = null)
        {
            if (!ClientTravelHooks.Ready) { _status = "Переход между локациями недоступен; смотрите журнал LAN"; return; }
            if (!LanSaveIsolation.Ready || ClientTravelHooks.LoadFailure != null)
            { _status = "Защита сохранений недоступна; перезапустите игру и смотрите журнал LAN"; return; }
            if (LanStorageGame.Failure != null || LanWorldItems.Failure != null || LanWorldWork.Failure != null || LanTrainBuild.Failure != null)
            { _status = "Общие хранилища и предметы недоступны в этой версии игры; смотрите журнал LAN"; return; }
            if (!LanWorldCatalog.Ready)
            { _status = LanWorldCatalog.Failure == null ? "Проверка ресурсов игры; повторите подключение чуть позже" : "Ресурсы игры изменились; нужен обновлённый каталог LAN"; return; }
            if (GlobalManager.global != null && GlobalManager.global.controlledChar != null)
            { _status = "Для подключения перезапустите игру и войдите из главного меню"; return; }
            // LAN: the six-digit 10-second PIN. Relay: the host's PIN or room password.
            string joinCode = _relayMode ? LanRelayText.NormalizeSecret(_relayJoinSecret) : (_joinCode ?? "").Trim();
            if (_relayMode ? !LanRelayText.ValidSecret(joinCode) : !LanLocalText.ValidPin(joinCode))
            { _status = _relayMode ? "Введите PIN или пароль комнаты от хоста (от 6 символов)" : "Введите шесть цифр свежего кода от хоста"; return; }
            IPAddress address = selectedAddress;
            LanRelayOptions relay = null;
            if (_relayMode) { if ((relay = RelayOptions(true)) == null) return; }
            else if (address == null && !string.IsNullOrWhiteSpace(_manualAddress) &&
                (!IPAddress.TryParse(_manualAddress.Trim(), out address) || address.AddressFamily != AddressFamily.InterNetwork))
            { _status = "Резервный адрес должен быть IPv4 адресом хоста"; return; }
            try
            {
                LanInventoryGuard.BackupSavesBeforeSession(Logger);
                StopNetwork();
                _hosting = false;
                _isolated = relay != null ? LanIsolatedSession.RelayJoin(joinCode, relay, NetworkGameVersion)
                    : LanIsolatedSession.Join(joinCode, address, NetworkGameVersion);
                _pairing = _isolated;
                _status = _pairing.Status;
                Logger.LogInfo(relay != null ? "Joining a relay room via the " + (CustomRelayConfigured ? "custom" : "built-in") + " relay; room code and secret omitted from logs"
                    : "Searching LAN for invitation; PIN omitted from logs");
            }
            catch (Exception ex)
            {
                _status = "Ошибка подключения: " + ex.Message;
                Logger.LogError(ex);
                StopSocketOnly();
            }
        }

        private void StopNetwork()
        {
            SendLeave();
            _lostAt = -1f;
            _zombies?.Dispose(); _zombies = null;
            _combatInventory = new HostCombatInventory();
            _peerWorkLimits = new LanPeerWorkLimits();
            _trainSync?.Freeze();
            _clock.Reset();
            if (!ClientTravelHooks.Loading && ClientTravelHooks.LoadFailure == null && LanSaveIsolation.Failure == null)
                _travel?.Complete();
            StopSocketOnly();
            _guestHost?.End(); _guestHost = null; _guestClient = null;
            LanStorageGame.CloseWindow(_storageClient); LanStorageGame.GuestOpenScene = null;
            LanWorldDoors.GuestToggle = null; LanWorldDoors.GuestUnlock = null; _doors = null; _quests = null; LanQuests.Request = null;
            _sceneStates?.Clear(); _sceneStates = null;
            LanDifficulty.LootActive = false; LanDifficulty.HostRestore(); LanDifficulty.GuestRestore();
            LanTakables.GuestAbort(); LanWorldItems.PartnerAvatar = null;
            LanSleep.Reset(); LanExplosions.Clear(); LanThrows.Clear(); LanThrows.GuestThrow = null; LanTrainDecor.GuestRequest = null; LanFuelStation.Clear();
            _worldItems?.ClearShown(); _worldItems = null; LanWorldItems.Dropped.Clear(); LanWorldItems.Placed.Clear(); LanWorldItems.GuestActive = false; LanWorldWork.GuestWork = null;
            LanTrainBuild.GuestAttached = false; LanTrainBuild.GuestBuild = null; LanTrainBuild.GuestPlace = null; LanTrainBuild.GuestFurniture = null; LanTrainBuild.GuestDismantle = null;
            LanTrainControl.GuestAttached = false; LanTrainControl.GuestRequest = null; LanTrainControl.ReleaseGuest(); _marks = null;
            _chat?.Close(); _chat = null;
            if (LanGuestDeath.Dead) LanGuestDeath.Respawn(); // never left dead without a session
            _deathSent = false; _deathLeftItems = false; _respawning = false; _guestDeadAt = -1f;
            _storageHost?.End(); _storageHost = null; _storageClient?.End(); _storageClient = null;
            _peer = null;
            _hosting = false;
            _connected = false;
            _joinCode = ""; _relayJoinSecret = "";
            _joinStartedGame = false;
            _startedLocalNewGame = false;
            _clientPlacedNearHost = false;
            _remoteScene = null;
            _trainSync = null;
            _session = 0;
            _stateSequence = 0;
            _remoteSequence = 0;
            _haveRemoteState = false;
            _remoteStateAt = 0;
            ClearPartnerExtras();
            _poses.Clear();
            _inventoryReadyCaptured = false;
            _remoteCarIndex = -1;
            _remoteEquippedItem = -1; _remoteShotItem = -1; _remoteShotSequence = 0;
            _worldEpoch = 0; _worldSequence = 0; _receivedWorldSequence = 0; _clientLoadEpoch = 0;
            _hostWorldScene = null; _hostSawLoading = false; _hostWorldReady = false; _hostReadySince = -1f;
            _clientWorld = null; _nextWorldSend = 0; _nextTravelAttempt = 0;
            _readySince = -1f;
            _status = "Не подключено";
            HideAvatar();
        }

        // 1.4.5: the partner learns at once that this player leaves (not after 30 s of silence).
        private void SendLeave()
        {
            if (_network == null || !_connected || _session == 0) return;
            try
            {
                for (int i = 0; i < 2; i++) SendPacket(new Packet { Kind = PacketKind.Leave }, _peer);
                _isolated?.Flush(400);
            }
            catch (Exception ex) { Logger.LogWarning("Leave not sent: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // The local player exits to the main menu: its session ends (the host's room closes with it).
        private void LeaveToMenu()
        {
            if (_network == null && _pairing == null) return;
            Logger.LogInfo(_hosting ? "Host left for the main menu; the session ends" : "Guest left for the main menu; the session ends");
            StopNetwork();
        }

        // The partner is gone: the host waits for a guest again, a guest waits for its host (at most
        // GuestWaitForHost, then it leaves too).
        private void PeerLost(string alert)
        {
            _trainSync?.Freeze();
            _zombies?.Freeze();
            _connected = false;
            _clock.Freeze();
            _network.Restart();
            if (_hosting) _peer = null;
            _remoteScene = null;
            _status = _hosting ? "Ожидание игрока" : "Связь потеряна; повторное подключение";
            HideAvatar();
            if (!_hosting && _lostAt < 0f) _lostAt = Time.unscaledTime;
            LanNotify.Alert(alert);
        }

        // Guest: the host left (or did not come back in time): back to the main menu.
        private const float GuestWaitForHost = 180f;
        private bool _menuPending;
        private void HostLeft(string reason)
        {
            bool inWorld = _clientSavePath != null && GlobalManager.global != null && GlobalManager.global.controlledChar != null;
            Logger.LogInfo(reason + "; the guest leaves the host's world");
            _connected = false; // nothing to tell a host that is gone
            StopNetwork();
            _status = reason + ". Чтобы играть дальше, перезапустите игру";
            LanNotify.Alert(reason);
            _menuPending = inWorld;
        }
        private void MenuWhenReady()
        {
            if (!_menuPending) return;
            var scenes = GlobalSceneManager.global;
            if (scenes == null || scenes.SceneCurrentlyLoading || ClientTravelHooks.Loading) return;
            _menuPending = false;
            try { if (!LanLeave.ToMainMenu()) Logger.LogWarning("Main menu not opened: the game's menu is not ready"); }
            catch (Exception ex) { Logger.LogWarning("Main menu not opened: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void StopSocketOnly()
        {
            _zombies?.Freeze();
            _isolated?.Dispose(); _isolated = null;
            _network = null; _pairing = null;
        }

        private void PollPairing()
        {
            if (_isolated != null && _isolated.SecurityFailure != null)
            {
                string reason = _isolated.SecurityFailure; StopNetwork();
                _status = reason + "; нужны новый код и разрешение хоста"; Logger.LogWarning(_status); return;
            }
            if (_pairing == null) return;
            _status = _pairing.Status;
            IPAddress peer;
            if (_pairing.TryTakeReady(out peer))
            {
                try
                {
                    _network = _isolated;
                    _trainSync = new TrainSync(packet => SendPacket(packet, _peer), Logger, _peerWorkLimits);
                    _zombies?.Dispose();
                    _zombies = new ZombieSync(_hosting, packet => SendPacket(packet, _peer), text => Logger.LogWarning("Zombies: " + text), _combatInventory);
                    _doors = new LanWorldDoors(_hosting, packet => SendPacket(packet, _peer), Logger.LogWarning);
                    if (_hosting) _doors.Ledger = GuestLedger;
                    _sceneStates?.Clear();
                    _sceneStates = new LanSceneStates(_hosting, packet => SendPacket(packet, _peer), Logger.LogWarning);
                    _quests = new LanQuests(_hosting, packet => SendPacket(packet, _peer));
                    _worldItems = new LanWorldItems(_hosting, packet => SendPacket(packet, _peer), Logger.LogWarning);
                    _marks = new LanMarks(_hosting, packet => SendPacket(packet, _peer));
                    _chat = new LanChat(_hosting, packet => SendPacket(packet, _peer));
                    _peer = _hosting ? null : new IPEndPoint(peer, Port);
                    _guestHost = _hosting ? new LanGuestHost(LanGuestPersistence.Store, packet => SendPacket(packet, _peer), Logger.LogInfo, new LanLedgerRules(), LanLedgerRules.HostBackpackIndex,
                        () => LanBeaverItem.Ready && LanBeaverSave.Ready ? new object[] { LanBeaverItem.ItemId, 1, new object[4] } : null) : null;
                    _guestClient = _hosting ? null : new LanGuestClient(GuestToken, packet => SendPacket(packet, _peer), Logger.LogInfo);
                    var hostWorld = _hosting ? new LanStorageHostWorld(GuestStoragePosition, _worldItems) : null;
                    if (hostWorld != null) hostWorld.GuestDied = GuestDied;
                    _storageHost = _hosting ? new LanStorageHost(hostWorld, packet => SendPacket(packet, _peer), Logger.LogInfo,
                        () => _connected && _guestHost != null && _guestHost.Confirmed) : null;
                    if (_storageHost != null) _storageHost.Ledger = GuestLedger;
                    if (_storageHost != null && LanQuests.Failure == null)
                    {
                        var quests = _quests;
                        _storageHost.Quests = quests;
                        _storageHost.QuestChanged = (kind, key) => quests.SendSoon();
                    }
                    if (_storageHost != null && LanTakables.Failure == null) _storageHost.Takables = new LanTakables();
                    WireCombat();
                    _storageClient = _hosting ? null : new LanStorageClient(new LanStorageGuestWorld(), packet => SendPacket(packet, _peer), Logger.LogInfo,
                        () => _guestClient?.CaptureSoon());
                    if (_storageClient != null)
                        _storageClient.PickedUp = (id, amount) => { _worldItems?.HidePickedItem(id); TrainLayout.HidePickedItem(id); };
                    if (_storageClient != null)
                        _storageClient.DeathDone = DeathAnswered;
                    if (_storageClient != null)
                        _storageClient.WorkDone = (kind, detail) =>
                        {
                            if (kind == LanStorage.WorkRefuel) LanWorldWork.Refuelled(detail);
                            else if (kind >= LanStorage.WorkWater) LanWorldWork.Watered(kind, detail);
                        };
                    if (_storageClient != null)
                        _storageClient.DoorDone = LanTrainControl.DoorDone;
                    if (_storageClient != null)
                        _storageClient.QuestDone = LanQuests.Answered;
                    _lastReceive = Time.unscaledTime;
                    _pairing = null;
                    Logger.LogInfo("Approved isolated network worker ready; transport keys remain inside AppContainer");
                }
                catch (Exception ex)
                {
                    StopSocketOnly();
                    _status = "Не удалось открыть защищённое соединение; создайте новый код";
                    Logger.LogError(ex);
                }
            }
            if (_pairing != null && _pairing.Finished)
            {
                // The owner may keep the public advertisement alive after PIN
                // expiry. A new user invitation disposes this entire worker.
                _status = _pairing.Status; _pairing = null;
            }
        }

        private void ReceivePackets()
        {
            var budget = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 64 && _network != null && budget.Elapsed.TotalMilliseconds < 3; i++)
            {
                try
                {
                    if (!_network.Available) break;
                    IPEndPoint from;
                    Packet packet;
                    if (!_network.TryReceive(out from, out packet)) continue;
                    if (!_hosting && packet.Kind == PacketKind.VersionMismatch && _peer != null && SameEndpoint(_peer, from))
                    {
                        StopNetwork(); _status = "Версии игры не совпадают";
                        break;
                    }
                    if (packet.GameVersion != NetworkGameVersion)
                    {
                        if (_hosting && packet.Kind == PacketKind.Hello) SendTo(PacketKind.VersionMismatch, from);
                        continue;
                    }
                    if (_hosting) ReceiveAsHost(from, packet);
                    else ReceiveAsClient(from, packet);
                }
                catch (SocketException ex) { NetworkWarning(ex); break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { NetworkWarning(ex); break; }
            }
        }

        private void ReceiveAsHost(IPEndPoint from, Packet packet)
        {
            if (packet.Kind == PacketKind.Hello)
            {
                if (packet.Session == 0 || packet.Session != _network.Session) return;
                if (!_connected || _peer == null || _session != packet.Session)
                {
                    _haveRemoteState = false;
                    ClearPartnerExtras();
                    _poses.Clear();
                    _trainSync?.Freeze();
                    _trainSync = new TrainSync(value => SendPacket(value, _peer), Logger, _peerWorkLimits);
                    ResetZombieConnection();
                    HideAvatar();
                    _guestHost?.Begin();
                    _connectionPanelClosed = false;
                    _storageHost?.Begin();
                    _chat?.Reset();
                    _guestDeadAt = -1f;
                    LanNotify.Alert("Друг подключился");
                }
                _peer = from;
                _session = packet.Session;
                _connected = true;
                _lastReceive = Time.unscaledTime;
                _status = _network.Relay ? "Защищённое соединение через сервер" : "Защищённое соединение: " + from.Address;
                _nextWorldSend = 0;
                SendTo(PacketKind.Welcome, from);
                return;
            }
            if (_peer == null || !SameEndpoint(_peer, from)) return;
            if (packet.Session != _session) return;
            if (packet.Kind == PacketKind.Leave) { Logger.LogInfo("The guest left the game"); PeerLost("Друг вышел из игры"); return; } // 1.4.5
            if (packet.Kind == PacketKind.Heartbeat) { _lastReceive = Time.unscaledTime; return; }
            if (packet.Kind == PacketKind.Chat) { _lastReceive = Time.unscaledTime; ReceiveChat(packet); return; }
            if (packet.Kind >= PacketKind.StorageRequest && packet.Kind <= PacketKind.StorageViewAck) { _lastReceive = Time.unscaledTime; _storageHost?.Receive(packet, Time.unscaledTime); return; }
            if (packet.Kind >= PacketKind.IdentityRequest && packet.Kind <= PacketKind.GuestRestoreAck) { _lastReceive = Time.unscaledTime; _guestHost?.Receive(packet, Time.unscaledTime); return; }
            if (packet.WorldEpoch != _worldEpoch || _worldEpoch == 0) return;
            if (packet.Kind == PacketKind.TrainLayoutAck)
            { _lastReceive = Time.unscaledTime; _trainSync?.Receive(packet, true); return; }
            if (packet.Kind == PacketKind.DoorToggle) { _lastReceive = Time.unscaledTime; _doors?.Receive(packet, GuestStoragePosition()); return; }
            if (packet.Kind == PacketKind.SceneUse) { _lastReceive = Time.unscaledTime; _sceneStates?.Receive(packet, GuestStoragePosition()); return; }
            if (packet.Kind == PacketKind.Revive) { _lastReceive = Time.unscaledTime; ReceiveRevive(packet); return; }
            if (packet.Kind == PacketKind.Sleep) { _lastReceive = Time.unscaledTime; ReceiveSleep(packet); return; }
            if (packet.Kind == PacketKind.Mark) { _lastReceive = Time.unscaledTime; _marks?.Receive(packet); return; }
            if (packet.Kind == PacketKind.ZombieAction || packet.Kind == PacketKind.ZombieHurtAck || packet.Kind == PacketKind.CombatProposal || packet.Kind == PacketKind.CombatReload)
            {
                _lastReceive = Time.unscaledTime;
                if (packet.Kind == PacketKind.ZombieAction)
                {
                    if (!_haveRemoteState) return;
                    if (packet.CarIndex >= 0)
                    {
                        // 1.0.7: a shot from a train car is placed with the host's own car, as the guest
                        // is: a world origin was a state period (0.1 s) of train travel off and refused.
                        var car = TrainSync.GetCar(packet.CarIndex);
                        if (car == null) return;
                        var origin = car.transform.TransformPoint(new Vector3(packet.X, packet.Y, packet.Z));
                        var aim = car.transform.TransformDirection(new Vector3(packet.AimX, packet.AimY, packet.AimZ)).normalized;
                        packet.X = origin.x; packet.Y = origin.y; packet.Z = origin.z; packet.AimX = aim.x; packet.AimY = aim.y; packet.AimZ = aim.z;
                        packet.CarIndex = -1;
                    }
                    else { packet.X += _remoteCombatOffset.x; packet.Y += _remoteCombatOffset.y; packet.Z += _remoteCombatOffset.z; }
                }
                _zombies?.Receive(packet); return;
            }
            if (packet.Kind != PacketKind.State) return;
            _lastReceive = Time.unscaledTime;
            if (_haveRemoteState && packet.Sequence <= _remoteSequence) return;
            if (!ValidateClientPose(packet)) return;
            // The guest stops its states while dead: the first one well after its death means it is back.
            if (_guestDeadAt >= 0f && Time.unscaledTime - _guestDeadAt > 3f) _guestDeadAt = -1f;
            SetRemoteState(packet);
        }

        private void ReceiveAsClient(IPEndPoint from, Packet packet)
        {
            if (_peer == null || !SameEndpoint(_peer, from)) return;
            if (packet.Kind == PacketKind.Busy)
            {
                if (!_connected) { _status = "Хост уже занят; ожидание свободной комнаты"; _network.RetryAfterBusy(); }
                return;
            }
            if (packet.Kind == PacketKind.VersionMismatch) { _status = "Версии игры не совпадают"; return; }
            if (packet.Kind == PacketKind.Welcome)
            {
                if (packet.Session == 0 || packet.Session != _network.Session) return;
                if (_session != packet.Session)
                {
                    _session = packet.Session;
                    _haveRemoteState = false;
                    ClearPartnerExtras();
                    _poses.Clear();
                    _worldEpoch = 0; _receivedWorldSequence = 0; _clientLoadEpoch = 0;
                    _clientWorld = null; _clock.Reset();
                    _trainSync?.Freeze();
                    _trainSync = new TrainSync(value => SendPacket(value, _peer), Logger, _peerWorkLimits);
                    ResetZombieConnection();
                    _guestClient?.Begin(Time.unscaledTime);
                    _connectionPanelClosed = false;
                    LanStorageGame.CloseWindow(_storageClient);
                    _storageClient?.Begin();
                    _chat?.Reset();
                    LanNotify.Alert("Хост разрешил подключение");
                }
                _connected = true;
                _lostAt = -1f;
                _lastReceive = Time.unscaledTime;
                _status = _network.RoomName.Length != 0 ? "Защищённое соединение с комнатой «" + _network.RoomName + "»; ожидание уровня хоста" : "Защищённое соединение; ожидание уровня хоста";
                return;
            }
            if (!_connected || packet.Session != _session) return;
            if (packet.Kind == PacketKind.Leave) { HostLeft("Хост вышел из игры"); return; } // 1.4.5
            if (packet.Kind == PacketKind.Heartbeat) { _lastReceive = Time.unscaledTime; return; }
            if (packet.Kind == PacketKind.Chat) { _lastReceive = Time.unscaledTime; ReceiveChat(packet); return; }
            if (packet.Kind >= PacketKind.StorageRequest && packet.Kind <= PacketKind.StorageViewAck) { _lastReceive = Time.unscaledTime; _storageClient?.Receive(packet); return; }
            if (packet.Kind >= PacketKind.IdentityRequest && packet.Kind <= PacketKind.GuestRestoreAck) { _lastReceive = Time.unscaledTime; _guestClient?.Receive(packet, Time.unscaledTime); return; }
            if (packet.Kind == PacketKind.WorldState) { ReceiveWorld(packet); return; }
            if (packet.WorldEpoch != _worldEpoch || _worldEpoch == 0) return;
            if (packet.Kind == PacketKind.TrainMotion || packet.Kind == PacketKind.TrainLayoutChunk)
            {
                if(packet.Kind==PacketKind.TrainMotion && (_clientWorld==null || packet.Train.Scene!=_clientWorld.Scene)) return;
                _lastReceive = Time.unscaledTime; _trainSync?.Receive(packet, false); return;
            }
            if (packet.Kind == PacketKind.DoorStates) { _lastReceive = Time.unscaledTime; _doors?.Receive(packet, null); return; }
            if (packet.Kind == PacketKind.SceneStates) { _lastReceive = Time.unscaledTime; _sceneStates?.Receive(packet, null); return; }
            if (packet.Kind == PacketKind.QuestStates) { _lastReceive = Time.unscaledTime; _quests?.Receive(packet); return; }
            if (packet.Kind == PacketKind.Revive) { _lastReceive = Time.unscaledTime; ReceiveRevive(packet); return; }
            if (packet.Kind == PacketKind.Sleep) { _lastReceive = Time.unscaledTime; ReceiveSleep(packet); return; }
            if (packet.Kind == PacketKind.Explosion)
            {
                _lastReceive = Time.unscaledTime;
                var scenes = GlobalSceneManager.global;
                if (scenes != null && packet.Scene == scenes.CurrentSceneName && _clientPlacedNearHost) LanExplosions.Receive(packet.Action, packet.Text, new Vector3(packet.X, packet.Y, packet.Z));
                return;
            }
            if (packet.Kind == PacketKind.WorldItems) { _lastReceive = Time.unscaledTime; _worldItems?.Receive(packet); return; }
            if (packet.Kind == PacketKind.Mark) { _lastReceive = Time.unscaledTime; _marks?.Receive(packet); return; }
            if (packet.Kind == PacketKind.ZombieState || packet.Kind == PacketKind.ZombieHurt || packet.Kind == PacketKind.CombatState || packet.Kind == PacketKind.ZombieCorpse || packet.Kind == PacketKind.ZombieEffect)
            { _lastReceive = Time.unscaledTime; _zombies?.Receive(packet); return; }
            if (packet.Kind != PacketKind.State || !_connected) return;
            _lastReceive = Time.unscaledTime;
            if (_haveRemoteState && packet.Sequence <= _remoteSequence) return;
            SetRemoteState(packet);
        }

        // 1.0.8: a sound for the partner's message; while the LAN panel hides the chat, the message
        // is shown at the top of the screen as well.
        private void ReceiveChat(Packet packet)
        {
            string text = _chat?.Receive(packet);
            if (text == null) return;
            LanNotify.Message(_panelOpen ? LanSkinPicker.PartnerLabel(_hosting) + ": " + (text.Length > 120 ? text.Substring(0, 120) + "…" : text) : null);
        }

        private void SetRemoteState(Packet packet)
        {
            // 1.4.7: a new count of throws is a throw; health dropping by a blow's worth at once (not a
            // bleeding tick) is a hit.
            if (_haveRemoteState)
            {
                if (packet.Throws != _remoteThrows) _throwPending = true;
                if (packet.Interacts != _remoteInteracts) _interactPending = true;
                if (packet.HealthMax > 0 && _remoteHealthNow - packet.HealthNow >= Math.Max(FlinchHealth, packet.HealthMax / 25)) _flinchPending = true;
            }
            _remoteThrows = packet.Throws; _remoteMotion = packet.Motion; _remoteInteracts = packet.Interacts;
            // 1.4.6: the walk and its pace come from the shown motion (UpdateRemoteAvatarPose).
            _poses.Push(packet);
            var nextLocal = new Vector3(packet.LocalX, packet.LocalY, packet.LocalZ);
            _remoteSequence = packet.Sequence;
            _haveRemoteState = true;
            _remoteStateAt = Time.unscaledTime;
            _remoteCarIndex = packet.CarIndex;
            _remoteLocalPosition = nextLocal;
            _remoteLocalYaw = packet.LocalYaw;
            _remoteScene = packet.Scene;
            _remotePosition = new Vector3(packet.X, packet.Y, packet.Z);
            _remoteYaw = packet.Yaw;
            _remoteTrainPosition = packet.TrainPosition;
            _remoteEquippedItem = packet.EquippedItemId;
            _remoteShotSequence = packet.ShotSequence;
            _remoteShotItem = packet.ShotItemId;
            _remoteHealth = packet.Health;
            _remoteHealthNow = packet.HealthNow; _remoteHealthMax = packet.HealthMax; _remoteSkin = packet.Skin;
            _remotePitch = packet.Pitch;
            _remoteCrouch = (packet.Pose & LanProtocol.PoseCrouch) != 0;
            _remoteAim = (packet.Pose & LanProtocol.PoseAim) != 0;
            bool wasDowned = _remoteDowned;
            _remoteDowned = (packet.Pose & LanProtocol.PoseDowned) != 0;
            _remoteDead = (packet.Pose & LanProtocol.PoseDead) != 0;
            _remoteHelmetLight = (packet.Pose & LanProtocol.PoseHelmetLight) != 0;
            _remoteGunLight = (packet.Pose & LanProtocol.PoseGunLight) != 0;
            _remoteReload = (packet.Pose & LanProtocol.PoseReload) != 0;
            LanSkinPicker.Partner = packet.Name ?? "";
            if (_remoteDowned && !wasDowned && !LanDowned.Down)
                LanNotify.Alert(LanSkinPicker.PartnerLabel(_hosting) + " тяжело ранен: подойдите и удерживайте E, чтобы поднять");
        }

        // 1.1.9: the partner's Revive packet, for this player when it lies downed.
        private void ReceiveRevive(Packet packet)
        {
            var scenes = GlobalSceneManager.global;
            if (scenes == null || packet.Scene != scenes.CurrentSceneName) return;
            LanDowned.Receive(packet.Action);
        }

        private void SendRevive(byte action)
        {
            var scenes = GlobalSceneManager.global;
            string scene = scenes == null ? null : scenes.CurrentSceneName;
            if (!_connected || _peer == null || _worldEpoch == 0 || string.IsNullOrEmpty(scene)) return;
            SendPacket(new Packet { Kind = PacketKind.Revive, WorldEpoch = _worldEpoch, Scene = scene, Sequence = ++_reviveSequence, Action = action }, _peer);
            if (_hosting && action == LanProtocol.ReviveDone) LanGuestCredits.Revive(); // 1.3.0: the guest gets up with some health
        }

        // 1.2.2: the partner's Sleep packet, in this world.
        private void ReceiveSleep(Packet packet)
        {
            var scenes = GlobalSceneManager.global;
            if (scenes == null || packet.Scene != scenes.CurrentSceneName) return;
            LanSleep.Receive(packet.Action, packet.Revision);
        }

        // 1.2.2: a world packet of this session (Sleep, Explosion) in this scene.
        private void SendWorldEvent(Packet packet)
        {
            var scenes = GlobalSceneManager.global;
            string scene = scenes == null ? null : scenes.CurrentSceneName;
            if (!_connected || _peer == null || _worldEpoch == 0 || string.IsNullOrEmpty(scene)) return;
            packet.WorldEpoch = _worldEpoch; packet.Scene = scene;
            SendPacket(packet, _peer);
        }

        private void CoopTick()
        {
            var scenes = GlobalSceneManager.global;
            string scene = scenes == null ? null : scenes.CurrentSceneName;
            bool attached = _connected && _network != null && _worldEpoch != 0 && !string.IsNullOrEmpty(scene) && (_hosting ? _hostWorldReady : _clientPlacedNearHost);
            _partnerHere = attached && _haveRemoteState && _remoteScene == scene && Time.unscaledTime - _remoteStateAt < 3f &&
                !_remoteDowned && !_remoteDead && !(_hosting && _guestDeadAt >= 0f) && !(!_hosting && LanTrainControl.HostDead);
            if (_sleepPartner == null) _sleepPartner = () => _partnerHere;
            if (_sleepSend == null) _sleepSend = (action, hours) => SendWorldEvent(new Packet { Kind = PacketKind.Sleep, Sequence = ++_sleepSequence, Action = action, Revision = hours });
            if (_explosionSend == null) _explosionSend = (action, name, at) => SendWorldEvent(new Packet { Kind = PacketKind.Explosion, Sequence = ++_explosionSequence, Action = action, Text = name, X = at.x, Y = at.y, Z = at.z });
            if (_mapPartner == null) _mapPartner = () => _partnerHere || _connected && _haveRemoteState && _remoteScene == GlobalSceneManager.global?.CurrentSceneName && Time.unscaledTime - _remoteStateAt < 3f ? RemoteWorldPosition() : (Vector3?)null;
            LanSleep.Hosting = _hosting;
            LanSleep.PartnerHere = attached ? _sleepPartner : null;
            LanSleep.Send = attached ? _sleepSend : null;
            LanExplosions.Send = _hosting && attached ? _explosionSend : null;
            LanMapMarker.Partner = attached ? _mapPartner : null;
            LanSleep.Tick();
            LanExplosions.Tick();
            LanThrows.Tick();
            // 1.3.0: what the host's world gives the guest counts toward its checked stats.
            if (_creditStats == null) _creditStats = () => { var book = GuestLedger(); return book == null ? null : book.Stats; };
            if (_creditHere == null) _creditHere = () => GuestStoragePosition() != null;
            LanGuestCredits.Stats = _hosting && _connected ? _creditStats : null;
            LanGuestCredits.GuestHere = _hosting && _connected ? _creditHere : null;
        }

        // 1.1.9: the partner can still revive this player: in this world and up, or (seen by the
        // host) a guest that died and comes back next to the host.
        private bool PartnerCanHelp()
        {
            if (!_connected || _network == null || !_haveRemoteState || _remoteDowned || (_hosting ? !_hostWorldReady : !_clientPlacedNearHost)) return false;
            var scenes = GlobalSceneManager.global;
            string scene = scenes == null ? null : scenes.CurrentSceneName;
            if (string.IsNullOrEmpty(scene) || _remoteScene != scene) return false;
            if (_hosting && _guestDeadAt >= 0f) return Time.unscaledTime - _guestDeadAt < LanDowned.DownedSeconds + 15f;
            if (_remoteDead || !_hosting && LanTrainControl.HostDead) return false;
            return Time.unscaledTime - _remoteStateAt < 5f;
        }

        private void ClearPartnerExtras()
        {
            _remoteDowned = _remoteDead = _remoteHelmetLight = _remoteGunLight = _remoteReload = false;
            LanSkinPicker.Partner = "";
        }

        private void ResetZombieConnection()
        {
            _zombies?.Dispose();
            _zombies = new ZombieSync(_hosting, packet => SendPacket(packet, _peer), text => Logger.LogWarning("Zombies: " + text), _combatInventory);
            WireCombat();
        }

        // Host: the ledger of the paired guest while it is usable.
        private LanGuestLedger GuestLedger() { return _connected && _guestHost != null && _guestHost.Confirmed ? _guestHost.Ledger : null; }
        private void WireCombat() { if (_zombies != null && _hosting) _zombies.Combat.Ledger = GuestLedger; }
        private bool ValidateClientPose(Packet packet)
        {
            if (!_hostWorldReady || packet.Scene != _hostWorldScene) return false;
            var manager = GlobalManager.global;
            if (manager == null || manager.controlledChar == null) return false;
            var position = new Vector3(packet.X, packet.Y, packet.Z);
            var reportedPosition = position;
            if (packet.CarIndex >= 0)
            {
                var car = TrainSync.GetCar(packet.CarIndex);
                if (car == null) return false;
                var local = new Vector3(packet.LocalX, packet.LocalY, packet.LocalZ);
                var projected = car.transform.TransformPoint(local);
                float speed = manager.controlledTrain == null ? 0 : Mathf.Abs(manager.controlledTrain.Speed);
                // Account for delayed rendering of a moving train, then use the
                // host's car transform as the position for combat validation.
                float apart = Vector3.Distance(position, projected);
                if (apart > 12f + Math.Min(speed, 500f) * .75f && !Persistent("car position " + apart.ToString("0.0") + " m off")) return false;
                position = projected;
            }
            if (!_haveRemoteState)
            {
                if (Vector3.Distance(position, manager.controlledChar.transform.position) > 200f) return false;
                _remoteMoveBudget = 6f;
            }
            else
            {
                var previous = _remotePosition;
                if (_remoteCarIndex >= 0)
                {
                    var previousCar = TrainSync.GetCar(_remoteCarIndex);
                    if (previousCar != null) previous = previousCar.transform.TransformPoint(_remoteLocalPosition);
                }
                float elapsed = Mathf.Clamp(Time.unscaledTime - _remoteMoveBudgetAt, 0, 2);
                float available = Mathf.Min(6f, _remoteMoveBudget + 20f * elapsed);
                float distance = Vector3.Distance(previous, position);
                if (distance > available)
                {
                    // A fall, a jump off a moving train: one late state must not lock the guest
                    // at its last accepted place for good (everything near it would be "too far").
                    if (!Persistent("jump " + distance.ToString("0.0") + " m")) return false;
                    available = distance;
                }
                _remoteMoveBudget = available - distance;
            }
            _remoteMoveBudgetAt = Time.unscaledTime;
            _remoteRejectedSince = -1f;
            _remoteCombatOffset = position - reportedPosition;
            packet.X = position.x; packet.Y = position.y; packet.Z = position.z;
            return true;
        }

        // True once the guest's states have been refused for 1.5 s in a row: its new place is taken as is.
        private bool Persistent(string why)
        {
            float now = Time.unscaledTime;
            if (_remoteRejectedSince < 0f) _remoteRejectedSince = now;
            if (now - _remoteRejectedSince >= 1.5f)
            {
                Logger.LogInfo("Guest position resynchronised (" + why + ")");
                return true;
            }
            if (now - _remoteRejectLogAt > 5f) { _remoteRejectLogAt = now; Logger.LogInfo("Guest state refused for now: " + why); }
            return false;
        }

        private void ObserveHostWorld()
        {
            var scenes = GlobalSceneManager.global;
            bool loading = scenes != null && (scenes.SceneCurrentlyLoading || ClientTravelHooks.Loading);
            if (loading) _hostSawLoading = true;
            if (loading || !TrainWorldAvailable())
            { _hostWorldReady = false; _hostReadySince = -1f; _zombies?.Freeze(); return; }
            if (_hostReadySince < 0f) _hostReadySince = Time.unscaledTime;
            if (Time.unscaledTime - _hostReadySince < 0.5f) { _hostWorldReady = false; return; }
            if (_worldEpoch == 0 || _hostSawLoading || _hostWorldScene != scenes.CurrentSceneName)
            {
                unchecked { ++_worldEpoch; if (_worldEpoch == 0) ++_worldEpoch; }
                _hostWorldScene = scenes.CurrentSceneName;
                _hostSawLoading = false;
                _trainSync?.Freeze();
                _trainSync = new TrainSync(packet => SendPacket(packet, _peer), Logger, _peerWorkLimits);
                _nextWorldSend = 0;
                _haveRemoteState = false;
                _poses.Clear(); HideAvatar();
                Logger.LogInfo("Host world ready: epoch=" + _worldEpoch + " scene=" + _hostWorldScene);
            }
            _hostWorldReady = true;
        }

        private void SendWorld()
        {
            var manager = GlobalManager.global;
            var train = manager == null ? null : manager.controlledTrain;
            var saves = Zompiercer.SaveLoad.SaveLoadCore.global;
            if (train == null || train.Cars == null || train.Cars.Length == 0 || train.Cars[0].FrontAxis == null || saves == null) return;
            var rails = train.Spline == null ? null : train.Spline.GetComponent<ObjectID>();
            ClockSnapshot clock;
            if (!WorldClockSync.Capture(out clock)) clock = null;
            SendPacket(new Packet { Kind = PacketKind.WorldState, Sequence = ++_worldSequence, Scene = _hostWorldScene,
                Location = saves.currentLocation, RailsId = rails == null ? -1 : rails.ID,
                TrainPosition = train.Cars[0].FrontAxis.Position, Clock = clock, Difficulty = LanDifficulty.Capture() }, _peer);
            _nextWorldSend = Time.unscaledTime + 0.25f;
        }

        private void ReceiveWorld(Packet packet)
        {
            if (packet.Sequence <= _receivedWorldSequence || packet.WorldEpoch < _worldEpoch) return;
            if (!ClientWorldValidation.Valid(packet)) return;
            if (_clientWorld != null && packet.WorldEpoch == _worldEpoch &&
                (packet.Scene != _clientWorld.Scene || packet.Location != _clientWorld.Location)) return;
            _lastReceive = Time.unscaledTime;
            _receivedWorldSequence = packet.Sequence;
            if (_worldEpoch != packet.WorldEpoch)
            {
                if(!_peerWorkLimits.WorldChange(Time.unscaledTime)) return;
                _zombies?.Freeze();
                _trainSync?.Freeze();
                _worldEpoch = packet.WorldEpoch;
                _trainSync = new TrainSync(value => SendPacket(value, _peer), Logger, _peerWorkLimits);
                _clock.Reset();
                _haveRemoteState = false; _poses.Clear(); HideAvatar();
                _clientPlacedNearHost = false; _readySince = -1f;
                _remoteScene = packet.Scene;
                _remoteCarIndex = -1;
                _nextTravelAttempt = 0;
                Logger.LogInfo("Received host world: epoch=" + _worldEpoch + " scene=" + packet.Scene);
                // 1.0.3: the panel folds away as soon as the guest heads into the host's world.
                if (_panelOpen) CloseConnectedPanel();
            }
            _clientWorld = packet;
            _remoteTrainPosition = packet.TrainPosition;
            if (packet.Clock != null) _clock.Receive(packet.Clock, Time.unscaledTime);
            if (packet.Difficulty != null) LanDifficulty.GuestApply(packet.Difficulty); // 1.4.4
        }

        private void UpdateClientWorld()
        {
            if (_clientWorld == null || _clientLoadEpoch == _worldEpoch || Time.unscaledTime < _nextTravelAttempt) return;
            var scenes = GlobalSceneManager.global;
            var manager = GlobalManager.global;
            var saves = Zompiercer.SaveLoad.SaveLoadCore.global;
            if (scenes == null || manager == null || saves == null || scenes.SceneCurrentlyLoading || ClientTravelHooks.Loading || saves.LoadingNow) return;
            if (_travel.Active && !_travel.LoadFinished()) return;
            _nextTravelAttempt = Time.unscaledTime + 1f;
            try
            {
                if (manager.controlledChar == null)
                {
                    StartLocalGame();
                    if (_joinStartedGame) _clientLoadEpoch = _worldEpoch;
                }
                else if (_clientSavePath == null || !ClientTravelHooks.ClientSession)
                {
                    StopNetwork(); _status = "Переход отменён: сохранения клиента не изолированы";
                }
                else if (BeginClientTravel())
                {
                    _clientLoadEpoch = _worldEpoch;
                    _status = _travel.Status;
                }
                else if (!string.IsNullOrEmpty(_travel.Status)) _status = _travel.Status;
            }
            catch (Exception ex)
            {
                _nextTravelAttempt = Time.unscaledTime + 5f;
                _status = "Ошибка перехода за хостом: " + ex.Message;
                Logger.LogError(ex);
            }
        }

        private bool BeginClientTravel()
        {
            // Scene travel must retain the actual gameplay state, not the LAN panel's hold.
            ReleaseLanInput();
            return _travel.Begin(_clientWorld);
        }

        private void StartLocalGame()
        {
            var manager = GlobalManager.global;
            var scenes = GlobalSceneManager.global;
            var gui = manager == null ? null : manager.canvas;
            if (gui == null) gui = Object.FindObjectOfType<Zompiercer.GUI.GUIManager>();
            if (manager == null || gui == null || scenes == null || string.IsNullOrEmpty(_remoteScene)) return;
            if (manager.controlledChar != null) { _joinStartedGame = true; return; }
            if (gui.mainGameMenu == null || !gui.mainGameMenu.activeInHierarchy) return;
            if (!ClientWorldValidation.Valid(_clientWorld))
            {
                _status = "Сцена хоста отсутствует: " + _remoteScene;
                return;
            }
            try
            {
                var saves = Zompiercer.SaveLoad.SaveLoadCore.global;
                if (saves == null) return;
                string clientSavePath = Path.Combine(Paths.BepInExRootPath, "LAN-sessions", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N"));
                if (!LanSaveIsolation.Activate(clientSavePath))
                    throw new IOException("Не удалось изолировать сохранения; требуется перезапуск");
                _clientSavePath = clientSavePath;
                ClientTravelHooks.ClientSession = true;
                saves.currentLocation = _clientWorld.Location;
                saves.TrainPositionAfterLoad = -1f;
                saves.TrainRailsIDAfterLoad = -1;
                manager.GameStarted = true;
                manager.GameStartedTime = Time.time;
                GlobalSceneManager.SaveLocationStartFileAfterSceneLoaded = false;
                ClientTravelHooks.AuthorizeLoad(_clientWorld);
                scenes.LoadLevel(_clientWorld.Scene, _clientWorld.TrainPosition, _clientWorld.RailsId);
                _joinStartedGame = true;
                _startedLocalNewGame = true;
                _status = "Загрузка сцены хоста: " + _remoteScene;
                Logger.LogInfo("Loading host scene " + _remoteScene + " at train position " + _remoteTrainPosition);
            }
            catch (Exception ex)
            {
                if (LanSaveIsolation.Active) ClientTravelHooks.Fail(ex.GetType().Name);
                _status = "Не удалось запустить игру: " + ex.Message;
                Logger.LogError(ex);
            }
        }

        private void SendState()
        {
            if (!_hosting && !_clientPlacedNearHost) return;
            var manager = GlobalManager.global;
            var scenes = GlobalSceneManager.global;
            if (_hosting ? !_hostWorldReady : !LocalWorldReady()) return;
            string scene = scenes.CurrentSceneName;
            if (string.IsNullOrEmpty(scene)) return;
            var transform = manager.controlledChar.transform;
            var position = transform.position;
            var train = manager.controlledTrain;
            float trainPosition = train == null || train.Cars == null || train.Cars.Length == 0 ? 0f : train.Cars[0].FrontAxis.Position;
            var packet = new Packet
            {
                Kind = PacketKind.State, GameVersion = NetworkGameVersion, Scene = scene, Sequence = ++_stateSequence,
                X = position.x, Y = position.y, Z = position.z, Yaw = transform.eulerAngles.y,
                TrainPosition = trainPosition,
                EquippedItemId = RemoteEquipment.CaptureLocalItemId(),
                ShotSequence = RemoteEquipment.LocalShotSequence, ShotItemId = RemoteEquipment.LastShotItemId,
                Health = LocalHealth(), Skin = LanSkinPicker.Chosen,
                Pitch = LocalPitch(manager.controlledChar),
                Pose = (byte)((manager.controlledChar.crouch ? LanProtocol.PoseCrouch : 0) | (LocalAiming(manager.controlledChar) ? LanProtocol.PoseAim : 0) | LocalPoseExtras(manager.controlledChar)),
                Name = LanSkinPicker.NickName,
                SentAt = unchecked((uint)StateClock.ElapsedMilliseconds), Dash = RemoteEquipment.LocalDash,
                Motion = RemoteEquipment.LocalMotion, Throws = RemoteEquipment.LocalThrows, Interacts = RemoteEquipment.LocalInteracts
            };
            ushort healthNow, healthMax; LocalHealthPoints(out healthNow, out healthMax);
            packet.HealthNow = healthNow; packet.HealthMax = healthMax;
            var car = manager.controlledChar.OnTrainCar;
            if (car != null && train != null && car.transform.IsChildOf(train.transform))
            {
                int index = car.GetCarIndex();
                if (index >= 0 && index < LanProtocol.MaxCars)
                {
                    var local = car.transform.InverseTransformPoint(position);
                    packet.CarIndex = index; packet.LocalX = local.x; packet.LocalY = local.y; packet.LocalZ = local.z;
                    packet.LocalYaw = (Quaternion.Inverse(car.transform.rotation) * transform.rotation).eulerAngles.y;
                }
            }
            SendPacket(packet, _peer);
        }

        private void Send(PacketKind kind) { SendTo(kind, _peer); }

        private void SendTo(PacketKind kind, IPEndPoint endpoint)
        {
            SendPacket(new Packet { Kind = kind, GameVersion = NetworkGameVersion }, endpoint);
        }

        private void SendPacket(Packet packet, IPEndPoint endpoint)
        {
            if (_network == null || endpoint == null || !SameEndpoint(endpoint, _network.Peer)) return;
            if (packet.WorldEpoch != 0 && packet.WorldEpoch != _worldEpoch) return;
            try
            {
                packet.Session = packet.Kind == PacketKind.VersionMismatch ? _network.Session : _session;
                packet.GameVersion = NetworkGameVersion;
                packet.WorldEpoch = _worldEpoch;
                _network.Send(packet);
            }
            catch (Exception ex) { NetworkWarning(ex); }
        }

        private void NetworkWarning(Exception ex)
        {
            if (Time.unscaledTime < _nextNetworkWarning) return;
            _nextNetworkWarning = Time.unscaledTime + 5f;
            Logger.LogWarning("LAN network: " + ex.GetType().Name + ": " + ex.Message);
        }

        private void UpdateAvatar()
        {
            if (!_connected || string.IsNullOrEmpty(_remoteScene)) { HideAvatar(); return; }
            var manager = GlobalManager.global;
            var scenes = GlobalSceneManager.global;
            if (manager == null || manager.controlledChar == null || scenes == null || scenes.SceneCurrentlyLoading || ClientTravelHooks.Loading) { HideAvatar(); return; }
            if (!_hosting && _startedLocalNewGame)
            {
                var gui = manager.canvas;
                if (gui == null) gui = Object.FindObjectOfType<Zompiercer.GUI.GUIManager>();
                var introButton = gui == null ? null : gui.playAfterIntroductionButton;
                if (introButton != null && introButton.activeSelf)
                {
                    // The game's introduction pauses time and deliberately leaves the mouse unlocked.
                    // Use its own completion path before restoring control and moving the player.
                    scenes.PlayAfterIntroductionButton();
                    if (_clientWorld != null) Zompiercer.SaveLoad.SaveLoadCore.global.currentLocation = _clientWorld.Location;
                    Logger.LogInfo("Completed the new-game introduction for the joining player");
                }
            }
            if (!string.Equals(scenes.CurrentSceneName, _remoteScene, StringComparison.Ordinal))
            {
                _status = "Разные сцены: " + scenes.CurrentSceneName + " / " + _remoteScene;
                HideAvatar();
                return;
            }
            if (!_hosting && !_clientPlacedNearHost)
            {
                if (LanGuestDeath.Dead) return; // placed next to the host when it respawns
                if (_clientLoadEpoch != _worldEpoch || !_haveRemoteState) return;
                if (_travel.Active ? !_travel.LoadFinished() : !LocalWorldReady()) return;
                if (_trainSync == null || !_trainSync.Ready) { _status = "Получение поезда хоста"; return; }
                if (_startedLocalNewGame &&
                    (Zompiercer.SaveLoad.SaveLoadCore.global == null ||
                     Zompiercer.SaveLoad.SaveLoadCore.global.currentLocation == 0))
                {
                    _status = "Ожидание завершения загрузки";
                    return;
                }
                // A stored profile from the host decides the first placement.
                if (_guestClient != null && _guestClient.Waiting(Time.unscaledTime)) { _status = "Получение профиля игрока от хоста"; return; }
                var character = manager.controlledChar;
                Vector3 target = Vector3.zero;
                float passengerYaw = 0f;
                var profile = _guestClient == null ? null : _guestClient.Pending;
                bool passenger = _travel.Active && _travel.PassengerPosition(out target, out passengerYaw);
                // After a death: next to the host, never at a place saved in a profile.
                bool restoredPlace = !passenger && !_respawning && profile != null && SavedPlace(profile, character, out target, out passengerYaw);
                if (!passenger && !restoredPlace && !TryFindSpawn(character, out target)) { _status = "Ожидание свободного места рядом с хостом"; return; }
                foreach (var delayed in character.GetComponents<DelayedMoveToTrain>())
                { delayed.StopAllCoroutines(); Object.Destroy(delayed); }
                if (profile != null) RestoreGuestCharacter(_guestClient.TakePending());
                if (character.controller != null) character.controller.enabled = false;
                TrainSync.DetachPlayer();
                character.transform.position = target;
                if (passenger || restoredPlace)
                {
                    character.transform.rotation = Quaternion.Euler(0f, passengerYaw, 0f);
                    var camera = character.GetComponentInChildren<CameraController>();
                    if (camera != null) camera.charRotation = passengerYaw;
                }
                LastGround?.SetValue(character, target);
                UndergroundTimer?.SetValue(character, 0f);
                character.GravityVector = 0f;
                character.IgnoreNextFallDamage = true;
                if (character.controller != null) character.controller.enabled = true;
                _travel.Complete();
                _clientPlacedNearHost = true; _respawning = false;
                CloseConnectedPanel();
                _nativeHudPending = true;
                _status = restoredPlace ? "На сохранённом месте" : "Рядом с хостом";
                Logger.LogInfo((restoredPlace ? "Placed joining player at the saved place " : "Placed joining player next to host at ") + target);
            }
            if (_hosting && _guestDeadAt >= 0f) { HideAvatar(); return; }
            // 1.1.6: the partner chose another look (a new avatar at most every 3 seconds).
            if (_avatar != null && _avatarSkin != _remoteSkin && Time.unscaledTime - _avatarCreatedAt > 3f) HideAvatar();
            if (_avatar == null) CreateAvatar();
            if (_avatar == null) return;
            _avatar.SetActive(true);
            if (_remoteEquipment == null) _remoteEquipment = _avatar.AddComponent<RemoteEquipmentVisual>();
            _remoteEquipment.Apply(_remoteEquippedItem, _remoteShotSequence, _remoteShotItem);
            // 1.4.7: how it moves; eating, drinking, bandaging or swimming it puts its weapon away;
            // swimming or climbing it does not walk, in the air or in water its steps are not heard.
            byte use = (byte)(_remoteMotion >> LanProtocol.UseShift);
            bool swim = (_remoteMotion & LanProtocol.MotionSwim) != 0, air = (_remoteMotion & LanProtocol.MotionAir) != 0;
            _remoteEquipment.Hidden = use != LanProtocol.UseNone || swim;
            bool climbing = _avatarMotion != null && _avatarMotion.IsClimbing;
            bool walking = _remoteSpeed > WalkingSpeed && !swim && !climbing;
            if (_avatarMotion != null)
            {
                _avatarMotion.Moves((_remoteMotion & LanProtocol.MotionRun) != 0, air, swim, (_remoteMotion & LanProtocol.MotionCock) != 0, use);
                if (_throwPending && !_avatarLying) _avatarMotion.Throw();
                if (_flinchPending && !_avatarLying) _avatarMotion.Flinch();
                // 1.4.8: the held melee weapon's kind of strike, jabs with bare fists, the use key.
                _avatarMotion.StrikeKind = RemoteAvatarMotion.StrikeOf(_remoteEquippedItem);
                if (_remoteEquipment.Punches > _shownPunches && !_avatarLying) _avatarMotion.Jab();
                if (_interactPending && !_avatarLying) _avatarMotion.Interact();
            }
            _shownPunches = _remoteEquipment.Punches;
            _throwPending = _flinchPending = _interactPending = false;
            // 1.0.9: a firearm in hand gives the armed pose (aiming for a while after a shot), and the
            // walk keeps pace with the partner. 1.1.0: crouching, the view's pitch, melee swings.
            // 1.1.9: a downed or dead partner lies on its back; a reload; flashlights and steps.
            _avatarLying = _remoteDowned || _remoteDead || !_hosting && LanTrainControl.HostDead;
            if (_avatarMotion != null)
                _avatarMotion.Apply(_remoteEquipment.TwoHanded, _remoteEquipment.HeldLength, walking, _remoteSpeed, _remoteAim || Time.unscaledTime - _remoteEquipment.LastShotAt < 1.5f,
                    _remoteCrouch, _remotePitch, _remoteEquipment.Swings ? _remoteEquipment.LastShotAt : -100f,
                    _remoteEquipment.TwoHanded ? _remoteEquipment.LastShotAt : -100f, _remoteReload, _avatarLying);
            else if (_avatarAnimator != null) _avatarAnimator.SetBool("Walk", walking && !_avatarLying);
            if (_avatarExtras != null) _avatarExtras.Apply(_remoteHelmetLight, _remoteGunLight, _remotePitch, walking && !air, _remoteSpeed, _remoteCrouch, _avatarLying);
        }

        private Vector3 RemoteWorldPosition()
        {
            var car = _remoteCarIndex < 0 ? null : TrainSync.GetCar(_remoteCarIndex);
            return car == null ? _remotePosition : car.transform.TransformPoint(_remoteLocalPosition);
        }

        // 1.4.7: the partner's motion flags and throws (State.Motion, State.Throws), a throw or a hit
        // to show, a climb's start (in its car's space, or the world's); a hit takes this much health.
        private byte _remoteMotion, _remoteThrows, _remoteInteracts;
        private bool _throwPending, _flinchPending, _interactPending;
        private int _shownPunches; // 1.4.8: jabs of the partner's bare fists already shown
        private Vector3 _climbFrom;
        private int _climbCar = -1;
        private const int FlinchHealth = 4;

        // 1.4.6: above this shown ground speed (m/s) the avatar walks; once a minute the log tells
        // how far behind the partner's fastest delivered state it is shown (one send interval plus jitter).
        private const float WalkingSpeed = .3f;
        private float _nextDelayLog;

        private void UpdateRemoteAvatarPose()
        {
            Vector3 position; float yaw, speed; byte dash;
            if (!_poses.Get(out position, out yaw, out speed)) { _remoteSpeed = 0f; return; }
            _remoteSpeed = Mathf.Lerp(_remoteSpeed, Mathf.Min(speed, 15f), 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime));
            if (_poses.TakeDash(out dash) && _avatarMotion != null && !_avatarLying) _avatarMotion.Dash(dash);
            // 1.4.7: a ladder (the game moves the player to its top at once) is climbed from where the
            // partner stood, in the space of its car when it is on the train.
            var climbCar = _poses.ShownCar < 0 ? null : TrainSync.GetCar(_poses.ShownCar);
            if (_poses.TakeClimb() && _avatarMotion != null && !_avatarLying)
            {
                _avatarMotion.Climb();
                _climbCar = _poses.ShownCar;
                _climbFrom = climbCar != null ? climbCar.transform.InverseTransformPoint(_avatar.transform.position - Vector3.up * _avatarYOffset) : _avatar.transform.position - Vector3.up * _avatarYOffset;
            }
            if (_avatarMotion != null && _avatarMotion.IsClimbing && _climbCar == _poses.ShownCar)
            {
                var from = climbCar != null ? climbCar.transform.TransformPoint(_climbFrom) : _climbFrom;
                position = Vector3.Lerp(from, position, Mathf.SmoothStep(0f, 1f, _avatarMotion.ClimbShare));
            }
            if (Time.unscaledTime >= _nextDelayLog)
            {
                _nextDelayLog = Time.unscaledTime + 60f;
                Logger.LogInfo("Partner shown " + Mathf.RoundToInt(_poses.Delay * 1000f) + " ms behind its fastest state");
            }
            // 1.1.9: lying on its back, feet where it stands, a little above the ground.
            var rotation = _avatarLying ? Quaternion.Euler(0f, yaw, 0f) * Quaternion.Euler(-90f, 0f, 0f) : Quaternion.Euler(0f, yaw, 0f);
            _avatar.transform.SetPositionAndRotation(position + Vector3.up * (_avatarYOffset + (_avatarLying ? .15f : 0f)), rotation);
            if (_avatarExtras != null) _avatarExtras.Yaw = yaw;
        }

        // The first model whose ID equals one of the names; a name ending without a number also
        // matches as a prefix (any model of that kind).
        private static CharacterModel PickModel(System.Collections.Generic.List<CharacterModel> models, params string[] names)
        {
            foreach (var name in names)
                foreach (var model in models)
                    if (model.ID == name) return model;
            string prefix = names[names.Length - 1];
            foreach (var model in models)
                if (model.ID != null && model.ID.StartsWith(prefix, StringComparison.Ordinal)) return model;
            return null;
        }

        private void CreateAvatar()
        {
            GameObject staging = null;
            LanInventoryGuard.CaptureCheckpoint("before-remote-avatar", Logger);
            try
            {
                var npc = NPCManager.Global;
                var models = npc == null || npc.NPCModels == null ? new System.Collections.Generic.List<CharacterModel>() : npc.NPCModels.Where(item => item != null).ToList();
                // 1.0.2: the host is shown as a man and the guest as a woman (the game's own NPC
                // models). Without such a model: another model than the host's, or a tint.
                // 1.1.6: the look the partner chose in its main menu, when this game has that model.
                _avatarSkin = _remoteSkin; _avatarCreatedAt = Time.unscaledTime;
                string chosen = LanSkinPicker.ModelId(_remoteSkin);
                var model = chosen != null ? PickModel(models, chosen) : null;
                // 1.1.6: by default models whose clothes are whole (LanSkinPicker).
                if (model == null) model = _hosting
                    ? PickModel(models, LanSkinPicker.WomanDefault, "RegularWomanNPC_Military_04", "RegularWomanNPC_01", "RegularWomanNPC")
                    : PickModel(models, LanSkinPicker.ManDefault, "RegularMenNPC_Farmer_01", "RegularMenNPC_Worker_01", "RegularMenNPC");
                if (model == null && models.Count != 0) model = models[_hosting && models.Count > 1 ? 1 : 0];
                bool tint = _hosting && models.Count == 1;
                if (model != null)
                {
                    staging = new GameObject("LAN avatar staging");
                    staging.SetActive(false);
                    _avatar = DisplayCopy(model, staging.transform, "LAN Remote Player");
                    _avatarYOffset = 0f;
                    if (tint)
                        foreach (var renderer in _avatar.GetComponentsInChildren<Renderer>(true))
                            foreach (var material in renderer.materials) if (material.HasProperty("_Color")) material.color *= new Color(.65f, .8f, 1.15f, 1f);
                    Logger.LogInfo("Remote player shown as NPC model " + (string.IsNullOrEmpty(model.ID) ? model.name : model.ID) + (tint ? " (tinted)" : ""));
                    _avatarAnimator = _avatar.GetComponentInChildren<Animator>(true);
                    if (_avatarAnimator != null)
                    {
                        _avatarAnimator.runtimeAnimatorController = model.AnimationControllerNormal;
                        _avatarAnimator.enabled = true; _avatarAnimator.applyRootMotion = false;
                    }
                    // 1.0.9: the armed pose comes from an invisible military NPC playing the game's
                    // "NPC with AK" controller (RemoteAvatarMotion).
                    var soldier = PickModel(models.Where(item => item != model).ToList(), "MilitaryRegularNPC_01", "MilitaryRegularNPC");
                    GameObject driver = null;
                    if (soldier != null && soldier != model && npc.AnimatorControllerNPCWithAK != null)
                    {
                        try { driver = DisplayCopy(soldier, staging.transform, "LAN Remote Player pose"); }
                        catch (Exception ex) { Logger.LogWarning("Armed pose unavailable: " + ex.Message); }
                    }
                    RemoteAvatarMotion.Log = message => Logger.LogInfo(message);
                    _avatarMotion = _avatar.AddComponent<RemoteAvatarMotion>();
                    string poseProblem = _avatarMotion.Initialize(_avatarAnimator, driver, npc.AnimatorControllerNPCWithAK);
                    if (poseProblem != null) Logger.LogWarning("Armed pose unavailable: " + poseProblem);
                    // 1.1.9: its flashlights and steps.
                    try
                    {
                        string rig;
                        _avatarExtras = _avatar.AddComponent<RemoteAvatarExtras>();
                        _avatarExtras.Initialize(RemoteAvatarMotion.FindBone(_avatar.transform, "Hub003", out rig), _avatarMotion.RightPalm);
                    }
                    catch (Exception ex) { Logger.LogWarning("Partner flashlights and steps unavailable: " + ex.Message); _avatarExtras = null; }
                    _avatar.transform.SetParent(null, false);
                    _avatar.transform.position = RemoteWorldPosition();
                    _avatar.SetActive(true);
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("NPC avatar unavailable: " + ex.Message);
                if (_avatar != null) Object.Destroy(_avatar);
                _avatarAnimator = null; _avatarMotion = null; _avatarExtras = null;
            }
            finally
            {
                if (staging != null) Object.Destroy(staging);
                LanInventoryGuard.CaptureCheckpoint("after-remote-avatar", Logger);
            }
            _avatar = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            _avatar.name = "LAN Remote Player (placeholder)";
            _avatarYOffset = 1f;
            var capsuleCollider = _avatar.GetComponent<Collider>();
            if (capsuleCollider != null) capsuleCollider.enabled = false;
        }

        // A display-only copy of an NPC model under the inactive staging object (so Awake/OnEnable
        // wait until every gameplay script is gone): no scripts, colliders, physics, sounds or
        // cameras, and without the rifles baked into the model. Left inactive.
        internal static GameObject DisplayCopy(CharacterModel model, Transform staging, string name)
        {
            var copy = Object.Instantiate(model.gameObject, staging, false);
            copy.SetActive(false);
            copy.name = name;
            foreach (var behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(behaviour);
            if (copy.GetComponentsInChildren<MonoBehaviour>(true).Length != 0)
            {
                Object.Destroy(copy);
                throw new InvalidOperationException("Avatar still contains gameplay scripts");
            }
            foreach (var collider in copy.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (var body in copy.GetComponentsInChildren<Rigidbody>(true)) { body.isKinematic = true; body.detectCollisions = false; }
            foreach (var audio in copy.GetComponentsInChildren<AudioSource>(true)) audio.enabled = false;
            foreach (var camera in copy.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
            foreach (var child in copy.GetComponentsInChildren<Transform>(true))
                if (child.name == "Pickup_AKM_LOD0" || child.name == "Pickup_HuntingRifle_LOD0") child.gameObject.SetActive(false);
            return copy;
        }

        private void HideAvatar()
        {
            if (_avatar != null) { Object.Destroy(_avatar); _avatar = null; }
            _avatarAnimator = null;
            _avatarMotion = null;
            _avatarExtras = null;
            _remoteEquipment = null;
        }

        private bool LocalWorldReady()
        {
            var manager = GlobalManager.global;
            var scenes = GlobalSceneManager.global;
            var character = manager == null ? null : manager.controlledChar;
            // Control held by this mod's panel or by an open game window (inventory, a host
            // storage, construction) still counts as ready: the character is where it is, and
            // the host must keep seeing it (an open host storage closes without its position).
            if (character == null || scenes == null || scenes.SceneCurrentlyLoading || ClientTravelHooks.Loading ||
                !character.controlEnabled && _lanHeldPlayer != character && !GameWindowOpen() && !LanHoldsPlayer() || character.GetComponent<DelayedMoveToTrain>() != null ||
                character.transform.position == Vector3.zero)
            { _readySince = -1f; return false; }
            if (_readySince < 0f) _readySince = Time.unscaledTime;
            return Time.unscaledTime - _readySince > 3f;
        }

        // 1.4.1: control held by a LAN feature that keeps the player where it is (downed, waiting in bed
        // for the partner, the robot dog window). The host must keep seeing it: its pose says it is
        // downed (the partner's revive prompt), and its place keeps the host's requests answered.
        private static bool LanHoldsPlayer() { return LanDowned.Down || LanSleep.Waiting || LanTakables.WindowOpen; }

        private static bool GameWindowOpen()
        {
            var inventory = Zompiercer.Inventory.InventoryController.global;
            if (inventory != null && !inventory.IsAllInventoryClosed()) return true;
            var input = Zompiercer.Inputs.InputController.Global;
            return input != null && (int)input.CurrentState != 1; // a UI state of the game
        }

        private static bool TrainWorldAvailable()
        {
            var manager = GlobalManager.global;
            var scenes = GlobalSceneManager.global;
            var save = Zompiercer.SaveLoad.SaveLoadCore.global;
            return manager != null && manager.controlledChar != null && scenes != null &&
                !scenes.SceneCurrentlyLoading && !ClientTravelHooks.Loading && save != null && !save.LoadingNow && save.currentLocation > 0 &&
                manager.controlledChar.GetComponent<DelayedMoveToTrain>() == null;
        }

        private bool TryFindSpawn(ZombieFighterController character, out Vector3 target)
        {
            for (int ring = 0; ring < 3; ring++)
            for (int i = 0; i < 8; i++)
            {
                var candidate = RemoteWorldPosition() + Quaternion.Euler(0f, _remoteYaw + i * 45f, 0f) * Vector3.right * (2f + ring * 1.5f);
                if (GroundSpot(character, candidate, 3f, 8f, out target)) return true;
            }
            target = Vector3.zero;
            return false;
        }

        // Walkable ground below `candidate` (searched from `above` metres higher,
        // `depth` metres down) with room for the character's capsule.
        private static bool GroundSpot(ZombieFighterController character, Vector3 candidate, float above, float depth, out Vector3 target)
        {
            var controller = character.controller;
            float radius = controller == null ? 0.4f : controller.radius;
            float height = controller == null ? 1.8f : Mathf.Max(controller.height, radius * 2f);
            Vector3 center = controller == null ? Vector3.up * height * 0.5f : controller.center;
            var hits = Physics.RaycastAll(candidate + Vector3.up * above, Vector3.down, depth, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance);
            foreach (var hit in hits)
            {
                if (hit.normal.y < 0.65f || hit.collider.transform.IsChildOf(character.transform)) continue;
                target = hit.point + Vector3.up * (height * 0.5f - center.y + 0.12f);
                var middle = target + center;
                var upper = middle + Vector3.up * (height * 0.5f - radius);
                var lower = middle - Vector3.up * (height * 0.5f - radius);
                if (Physics.OverlapCapsule(lower, upper, radius, ~0, QueryTriggerInteraction.Ignore)
                    .Any(c => !c.transform.IsChildOf(character.transform))) continue;
                return true;
            }
            target = Vector3.zero;
            return false;
        }

        // The place stored in the guest profile, if it is in the current scene and
        // location and still walkable. Train passengers return to the same car.
        private static bool SavedPlace(object[] profile, ZombieFighterController character, out Vector3 target, out float yaw)
        {
            target = Vector3.zero; yaw = 0f;
            var scenes = GlobalSceneManager.global; var saves = Zompiercer.SaveLoad.SaveLoadCore.global;
            if (scenes == null || saves == null || (string)profile[1] != scenes.CurrentSceneName || (int)profile[2] != saves.currentLocation) return false;
            Vector3 place;
            int carIndex = (int)profile[3];
            if (carIndex >= 0)
            {
                var car = TrainSync.GetCar(carIndex);
                if (car == null) return false;
                place = car.transform.TransformPoint(new Vector3((float)profile[8], (float)profile[9], (float)profile[10]));
                yaw = (car.transform.rotation * Quaternion.Euler(0f, (float)profile[11], 0f)).eulerAngles.y;
            }
            else { place = new Vector3((float)profile[4], (float)profile[5], (float)profile[6]); yaw = (float)profile[7]; }
            return GroundSpot(character, place, 1.5f, 4f, out target);
        }

        private void RestoreGuestCharacter(object[] profile)
        {
            LanInventoryGuard.CaptureCheckpoint("before-guest-restore", Logger);
            try
            {
                LanGuestCharacter.Apply(profile);
                _guestClient.Applied(true, "Персонаж восстановлен из профиля у хоста; прогресс сохраняется у хоста");
                Logger.LogInfo("Guest character restored from the host's profile");
            }
            catch (Exception ex)
            {
                _guestClient.Applied(false, ex.GetType().Name);
                Logger.LogError("Guest character restore failed: " + ex);
            }
            LanInventoryGuard.CaptureCheckpoint("after-guest-restore", Logger);
        }

        private byte[] GuestToken(Guid world)
        {
            if (_identity == null)
            {
                if (_identityFailed) return null;
                try { _identity = LanPlayerIdentity.LoadOrCreate(LanPlayerIdentity.DefaultDirectory); }
                catch (Exception ex)
                {
                    _identityFailed = true;
                    Logger.LogWarning("Guest identity unavailable (" + LanPlayerIdentity.DefaultDirectory + "): " + ex.GetType().Name + ": " + ex.Message);
                    return null;
                }
            }
            return _identity.WorldToken(world);
        }

        // Guest (0.12.0): tell the host where it fell, then come back next to the host.
        private void UpdateGuestDeath()
        {
            if (!LanGuestDeath.Dead) { _deathSent = false; return; }
            var client = _storageClient;
            if (!_deathSent && client != null && _connected && Time.unscaledTime >= _deathRetryAt)
            {
                _deathSent = client.Death(LanGuestDeath.Point.x, LanGuestDeath.Point.y, LanGuestDeath.Point.z, LanGuestDeath.Car);
                if (!_deathSent) { _deathRetryAt = float.MaxValue; LanGuestDeath.Answer(LanStorage.Invalid, 0); }
            }
            if (!LanGuestDeath.ReadyToRespawn) return;
            LanGuestDeath.Respawn();
            _clientPlacedNearHost = false; _readySince = -1f; _deathSent = false; _deathRetryAt = 0f; _respawning = true;
            if (_deathLeftItems) _marks?.Stash(LanGuestDeath.Car, LanGuestDeath.Point, "Ваши вещи");
            _deathLeftItems = false;
        }
        private void DeathAnswered(byte status, int stacks)
        {
            if (status == LanStorage.Busy) { _deathSent = false; _deathRetryAt = Time.unscaledTime + 3f; return; }
            LanGuestDeath.Answer(status, stacks);
            _deathLeftItems = status == LanStorage.Ok && stacks > 0;
            // No more states while dead: the host hides the body; placement waits for the respawn.
            _clientPlacedNearHost = false;
            Logger.LogInfo("Guest death answered: status " + status + ", " + stacks + " item stack(s) left");
        }
        // Host (0.12.0): the guest died; its belongings lie in bags there.
        private void GuestDied(int car, Vector3 point)
        {
            _guestDeadAt = Time.unscaledTime;
            LanGuestCredits.Respawn(); // 1.3.0: it comes back with fresh health and needs
            _remoteDowned = false; _remoteDead = true; // until its first state after the respawn
            HideAvatar();
            _marks?.Stash(car, point, "Вещи друга");
            _marks?.Toast("Друг погиб. Его вещи остались в сумке на месте гибели");
        }

        // Host (1.1.7): the boss arena waits for the guest while it plays in this world, dead or alive.
        private void BossArenaTick()
        {
            bool hosting = _hosting && _connected && _network != null;
            bool guestHere = hosting && _hostWorldReady && _haveRemoteState && _remoteScene == _hostWorldScene && Time.unscaledTime - _remoteStateAt < 60f;
            bool dead = guestHere && _guestDeadAt >= 0f;
            LanBossArena.Tick(hosting, guestHere, guestHere && !dead ? GuestStoragePosition() : null, dead);
        }

        // 1.2.0: while a session is attached the guest's quests show the host's, and on the host a
        // quest goal counts for the guest too (its place, its ledger).
        private void QuestsTick()
        {
            bool hosting = _hosting && _connected && _network != null;
            LanQuests.Guest = !_hosting && _connected && _network != null && LanQuests.Failure == null;
            if (!LanQuests.Guest) LanQuests.Request = null;
            if (_questGuestPosition == null) _questGuestPosition = GuestStoragePosition;
            if (_questGuestCount == null) _questGuestCount = id => { var book = GuestLedger(); return book == null ? 0 : book.Count(id); };
            LanQuests.GuestPosition = hosting ? _questGuestPosition : null;
            LanQuests.GuestCount = hosting ? _questGuestCount : null;
            if (_questGuestProfile == null) _questGuestProfile = () => GuestLedger() != null ? _guestHost.ProfileId : null;
            LanQuests.GuestProfile = hosting ? _questGuestProfile : null; // 1.4.5
        }

        // 1.2.1: the guest's avatar holds what the guest carries; the guest sees what the host carries
        // in the host avatar's hands.
        private void TakablesTick()
        {
            bool hosting = _hosting && _connected && _network != null && LanTakables.Failure == null;
            if (_takableAvatar == null) _takableAvatar = () => _avatar != null && _avatar.activeSelf && !_avatarLying ? _avatar.transform : null;
            if (_takableProfile == null) _takableProfile = () => GuestLedger() != null ? _guestHost.ProfileId : null;
            if (_takableAble == null) _takableAble = () => GuestStoragePosition() != null && !_remoteDowned && !_remoteDead && _guestDeadAt < 0f;
            if (_takableCrouch == null) _takableCrouch = () => _remoteCrouch;
            if (_questGuestPosition == null) _questGuestPosition = GuestStoragePosition;
            LanTakables.Hosting = hosting;
            LanTakables.GuestAvatar = hosting ? _takableAvatar : null;
            LanThrows.GuestAvatar = hosting ? _takableAvatar : null;
            LanThrows.GuestUp = hosting ? _takableAble : null;
            LanTakables.GuestProfile = hosting ? _takableProfile : null;
            LanTakables.GuestPosition = hosting ? _questGuestPosition : null;
            LanTakables.GuestAble = hosting ? _takableAble : null;
            LanTakables.GuestCrouch = hosting ? _takableCrouch : null;
            LanWorldItems.PartnerAvatar = !_hosting && _connected ? _takableAvatar : null;
            LanTakables.HostTick();
            LanFuelStation.HostTick();
            // 1.4.4: zombie health and loot for two.
            LanDifficulty.ZombieHealth = _coopHealthEntry.Value; LanDifficulty.Loot = _coopLootEntry.Value;
            LanDifficulty.LootActive = _hosting && _connected;
            LanDifficulty.HostTick(_hosting && _connected, _hosting && GuestStoragePosition() != null);
        }

        // Host: where the guest stands (validated pose, car-relative while aboard).
        private Vector3? GuestStoragePosition()
        {
            if (!_connected || !_hostWorldReady || !_haveRemoteState || Time.unscaledTime - _remoteStateAt > 2f || _remoteScene != _hostWorldScene) return null;
            return RemoteWorldPosition();
        }

        // Guest: requests keep flowing while connected; opening needs the host's
        // world, a placed character and a profile the host keeps.
        private void UpdateGuestStorage()
        {
            var client = _storageClient;
            if (client != null && _connected) client.Tick(Time.unscaledTime);
            if (client != null) LanStorageGame.TickWindow(client);
            var scenes = GlobalSceneManager.global;
            bool usable = client != null && _connected && _clientPlacedNearHost && !LanGuestDeath.Dead && !_travel.Active && _guestClient != null && _guestClient.Saving &&
                LanStorageGame.Failure == null && TrainWorldAvailable() && _clientWorld != null && scenes != null && scenes.CurrentSceneName == _clientWorld.Scene;
            // 1.2.0: quests go to the host on the same terms as its storages.
            if (usable && _questRequest == null) _questRequest = (kind, key, scene) => _storageClient != null && _storageClient.Quest(kind, key, scene);
            LanQuests.Request = usable ? _questRequest : null;
            LanQuests.Unavailable = usable ? "" : _guestClient != null && _guestClient.Blocked ? "Квесты недоступны: ваш профиль у хоста не сохраняется"
                : "Квесты станут доступны, когда вы будете в мире хоста";
            if (!usable)
            {
                LanStorageGame.CloseWindow(client);
                LanTakables.GuestAbort();
                string reason = _guestClient != null && _guestClient.Blocked ? "Хранилища хоста недоступны: ваш профиль у хоста не сохраняется"
                    : "Хранилища хоста откроются, когда вы будете в его мире";
                LanStorageGame.GuestOpenScene = inventory => LanStorageGame.Notice(reason);
                // Work on the host's world is refused too (the guest's own copy is not used).
                LanWorldWork.GuestWork = client == null ? null : (Func<int, Vector3, int, int, object[], bool>)((kind, at, tool, extra, escrow) => { LanStorageGame.Notice(reason); return false; });
                LanTrainBuild.GuestBuild = null; LanTrainBuild.GuestPlace = null;
                LanTrainBuild.GuestFurniture = null; LanTrainBuild.GuestDismantle = null;
                LanTrainControl.GuestRequest = null;
                LanThrows.GuestThrow = null; LanTrainDecor.GuestRequest = null; LanFuelStation.GuestRequest = null;
                return;
            }
            LanStorageGame.GuestOpenScene = inventory => LanStorageGame.OpenWindow(client, LanStorageGame.SceneTarget(inventory), null, inventory);
            LanWorldWork.GuestWork = (kind, at, tool, extra, escrow) => client.Work(kind, at.x, at.y, at.z, tool, extra, escrow);
            LanTrainBuild.GuestBuild = (car, kind, identity, tool, needs) => client.Build(car, kind, identity, tool, needs);
            LanTrainBuild.GuestPlace = (part, car, type, point, pose) => client.Place(part, car, type, point, pose);
            // 1.4.2: furniture kits and taking the host's train apart.
            LanTrainBuild.GuestFurniture = (kit, car, pose, answer, giveBack) => client.Furniture(kit, car, pose, answer, giveBack);
            LanTrainBuild.GuestDismantle = (mode, car, owner, identity, tool, answer) => client.Dismantle(mode, car, owner, identity, tool, answer);
            LanTrainControl.GuestRequest = (kind, index, check, value, escrow) => client.Control(kind, index, check, value, escrow);
            // 1.4.0: the guest's explosives fly in the host's world; paint, wires and signs of the host's train.
            LanThrows.GuestThrow = (escrow, car, at, turn, type, answer, giveBack) => client.Throw(escrow, car, at, turn, type, answer, giveBack);
            LanTrainDecor.GuestRequest = (request, answer) => client.Decor(request, answer);
            // 1.4.3: the fuel stations of the host's scene.
            LanFuelStation.GuestRequest = (payload, answer) => client.Takable(payload, answer);
            LanTrainControl.Tick();
            // 1.2.1: a host object in the guest's hands, the guest's robot window.
            LanTakables.GuestTick(client);
            if (GameMenusVisible()) { LanStorageGame.CloseWindow(client); LanTakables.CloseWindow(); }
            if (LanTakables.Carrying || LanTakables.WindowOpen) return;
            if (LanStorageGame.WindowOpen || LanStorageGame.RecentlyClosed || _panelOpen) return;
            var input = Zompiercer.Inputs.InputController.Global;
            var inventories = Zompiercer.Inventory.InventoryController.global;
            if (input == null || (int)input.CurrentState != 1 || inventories == null || !inventories.IsAllInventoryClosed()) return;
            bool use = input.isKbMouseDevice ? input.isUseKeyDown : input.gp_isUseOpenTalkDown;
            // The game picks loose items up with its use key or its own take key.
            bool take = use || input.isTakeItemAccept_Down;
            // 1.2.1: a host barrel or robot dog in front of the guest.
            var near = LanWorldItems.InViewAny();
            if (near != null && near.Kind == LanWorldItems.TakableKind && LanTakables.GuestLook(near, use, input.isKbMouseDevice && input.isReloadDown, client)) return;
            var shown = near != null && near.Kind != LanWorldItems.TakableKind ? near : null;
            if (shown != null)
            {
                if (shown.Kind == LanWorldItems.BagKind)
                {
                    LanStorageGame.HintOpen(LanStorageGame.BagName());
                    if (use) LanStorageGame.OpenWindow(client, LanStorage.BagPayload(shown.HostId));
                }
                else
                {
                    LanStorageGame.HintPickUp(shown.ItemId, shown.Amount);
                    if (take && !client.Busy) client.Pickup(shown.HostId, shown.ItemId, shown.Amount);
                }
                return;
            }
            // Host blueprints to build and host fuel tanks to fill.
            if (LanTrainBuild.Tick(client)) return;
            // 1.1.6: doors of the host's train, through the host.
            if (LanTrainControl.TickDoor(use)) return;
            // Light switches of parts the host built (when the host lets the guest drive).
            if (LanTrainControl.TickLook(use)) return;
            // 1.4.11: an item on a shelf before the shelf's own storage.
            int hostId, itemId, itemAmount;
            if (TrainItemInView(out hostId, out itemId, out itemAmount))
            {
                LanStorageGame.HintPickUp(itemId, itemAmount);
                if (take && !client.Busy) client.Pickup(hostId, itemId, itemAmount);
                return;
            }
            Workbench bench;
            byte[] target = LanStorageGame.TrainTargetInView(out bench);
            if (target != null)
            {
                LanStorageGame.HintOpen(null);
                if (use) LanStorageGame.OpenWindow(client, target, bench);
            }
        }

        // 1.4.11: an item the host placed on its train, also one on a shelf whose copy is one box (LookAt).
        private static bool TrainItemInView(out int hostId, out int itemId, out int amount)
        {
            hostId = itemId = amount = 0;
            var seen = LanWorldItems.LookAt(LanWorldItems.Look, collider => { int id, item, count; return TrainLayout.TryIdentifyItem(collider, out id, out item, out count); });
            return seen != null && TrainLayout.TryIdentifyItem(seen, out hostId, out itemId, out amount);
        }

        // Guest: items its player dropped or placed this frame go to the host's world.
        private void BookDroppedItems()
        {
            var dropped = LanWorldItems.Dropped;
            if (dropped.Count == 0) return;
            var player = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var client = _storageClient;
            bool usable = !_hosting && _connected && client != null && player != null && _clientPlacedNearHost && _guestClient != null && _guestClient.Saving;
            for (int i = dropped.Count - 1; i >= 0; i--)
            {
                var item = dropped[i];
                if (item == null || item.inInventory != null) { dropped.RemoveAt(i); LanWorldItems.Placed.Remove(item); continue; } // picked back or destroyed
                if (!usable) { dropped.RemoveAt(i); LanWorldItems.Placed.Remove(item); continue; } // solo or unsaved: a local item like any other
                if (client.Busy) return; // one escrow at a time; it waits where it lies
                var at = item.transform.position;
                if ((at - player.transform.position).sqrMagnitude > 9f) at = player.transform.position + player.transform.forward * .8f + Vector3.up * .3f;
                object[] entry = null;
                try { entry = LanStorage.Normalize(new object[] { item.itemID, item.amount, item.Save() }); LanGuestCharacter.ValidateItems(new object[] { entry }, 0); }
                catch (Exception ex) { Logger.LogWarning("Dropped item cannot travel: " + ex.Message); entry = null; }
                dropped.RemoveAt(i);
                bool placed = LanWorldItems.Placed.Remove(item);
                var car = item.ParentedToTraincCar;
                int carIndex = TrainLayout.CarIndex(GlobalManager.global.controlledTrain, car);
                var position = carIndex < 0 ? at : car.transform.InverseTransformPoint(at);
                var rotation = carIndex < 0 ? item.transform.rotation : Quaternion.Inverse(car.transform.rotation) * item.transform.rotation;
                var pose = new[] { position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w };
                if (entry != null && (placed ? client.Drop(entry, carIndex, pose) : client.Drop(entry, at.x, at.y, at.z)))
                { item.amount = 0; item.gameObject.SetActive(false); Object.Destroy(item.gameObject); continue; }
                // Not sent: back into the backpack rather than a local-only item.
                if (player.inventory == null || !player.inventory.Add(item, true)) Logger.LogWarning("Dropped item kept locally");
                LanStorageGame.Notice("Этот предмет нельзя выбросить по сети");
            }
        }

        private void DrawGuestProfile()
        {
            var kit = _hosting && _guestHost != null && _connected ? _guestHost.PendingKit : null;
            if (kit != null)
            {
                GUILayout.Label("Новый друг пришёл с вещами (их источник хост не видит):");
                GUILayout.Label(LanLedgerRules.Describe(kit));
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Принять вещи")) _guestHost.AcceptKit(true);
                if (GUILayout.Button("Без вещей")) _guestHost.AcceptKit(false);
                GUILayout.EndHorizontal();
            }
            if (_hosting && _storageHost != null && _storageHost.Open) GUILayout.Label("Друг смотрит в хранилище");
            if (_hosting)
            {
                bool drive = GUILayout.Toggle(LanTrainControl.HostAllows, " Друг может управлять поездом (тяга, тормоз, двигатель, свет, ремонт рычагов)");
                if (drive != LanTrainControl.HostAllows) { LanTrainControl.HostAllows = drive; _friendDrivesEntry.Value = drive; }
            }
            else if (_connected) GUILayout.Label(LanTrainControl.GuestAllowed ? "Хост разрешил вам управлять поездом" : "Управлять поездом может только хост");
            if (_connected && _chat != null)
            {
                GUILayout.Label(_chat.Hint);
                bool hide = GUILayout.Toggle(LanChat.HidePartner, " Скрыть сообщения " + (_hosting ? "друга" : "хоста"));
                if (hide != LanChat.HidePartner) { LanChat.HidePartner = hide; _chatHideEntry.Value = hide; }
            }
            if (_connected) GUILayout.Label("Метка: " + (LanMarks.Key == KeyCode.Mouse2 ? "средняя кнопка мыши" : LanMarks.Key.ToString()) + " (враг, находка или «сюда»); повторно по своей метке — убрать");
            if (_hosting && _guestHost != null && _connected) GUILayout.Label(_guestHost.Status);
            else if (!_hosting && _guestClient != null && _connected) GUILayout.Label(_guestClient.Status);
            if (_hosting && LanGuestPersistence.LastResult != null) GUILayout.Label(LanGuestPersistence.LastResult);
        }

        private void LogLocalEquipment()
        {
            if (!_connected || Time.unscaledTime < _nextWeaponCheck) return;
            _nextWeaponCheck = Time.unscaledTime + 1f;
            var character = GlobalManager.global == null ? null : GlobalManager.global.controlledChar;
            var inventory = Zompiercer.Inventory.InventoryController.global;
            if (character == null || inventory == null || character.zombieFighterFireArmWeapon == null) return;
            if (!_inventoryReadyCaptured && LocalWorldReady())
            {
                _inventoryReadyCaptured = true;
                LanInventoryGuard.CaptureCheckpoint("local-world-ready", Logger);
            }
            var firearm = character.zombieFighterFireArmWeapon;
            string belt = character.beltInventory == null ? "missing" : string.Join("|", character.beltInventory.Select(slot =>
                slot == null ? "null" : string.Join(",", slot.content.Select(item => item == null ? "null" : item.itemID.ToString()).ToArray())).ToArray());
            var gun = firearm.gunList.FirstOrDefault(g => g != null && g.ID == firearm.SelectedWeaponID);
            string state = "slot=" + inventory.selectedBeltIcon + " belt=" + belt + " selected=" + firearm.SelectedWeaponID +
                " active=" + (gun != null && gun.gameObject.activeInHierarchy) + " control=" + character.controlEnabled +
                " lowered=" + (character.weaponAnimator != null && character.weaponAnimator.Down);
            if (state == _lastWeaponState) return;
            _lastWeaponState = state;
            Logger.LogInfo("Local equipment: " + state);
            LanInventoryGuard.CaptureCheckpoint("local-equipment-changed", Logger);
        }

        private static bool SameEndpoint(IPEndPoint left, IPEndPoint right)
        {
            return left != null && right != null && left.Port == right.Port && left.Address.Equals(right.Address);
        }

    }
}
