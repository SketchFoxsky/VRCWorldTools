using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace SketchFoxsky.Uno
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class UnoGameManager : UdonSharpBehaviour
    {
        #region Fields

        [Header("Card Pool")]
        public UnoCard[] unoCards;

        [Header("Positions")]
        public Transform DeckTransform;
        public Transform LastPlayedCardTransform;

        [Header("Player Hands")]
        public UnoPlayerHand[] unoPlayers;

        [Header("Rules")]
        public int MaxCardsPerHand = 30;

        [Header("Dealing")]
        public int StartingHandSize = 7;

        [Header("Visuals")]
        public Material CardMaterial;
        public Material HiddenCardMaterial;
        public float DealFlyDuration = 0.18f;
        public float PlayedFlyDuration = 0.18f;

        [Header("Tick Rates")]
        public float ClientLayoutTick = 0.15f;
        public float MasterTick = 0.20f;

        #endregion

        #region Masters Variables
        [UdonSynced] private int[] _playerIds;        // seat -> playerId (-1 empty)
        [UdonSynced] private int[] _handCardIndex;    // seat*cap + slot -> cardIndex (-1 empty)
        [UdonSynced] private int _lastPlayedCardIndex = -1;
        [UdonSynced] private bool _matchStarted;
        [UdonSynced] private int _stateSeq;

        #endregion

        #region Local only variables

        private int _seatCount;
        private int _handCapacity;

        private int[] _deckOrder;
        private int _deckTop;

        private int[] _seatCardBuffer;
        private bool[] _cardUsed;
        private int[] _relayLastSeq;

        private float _masterTimer;
        private float _clientTimer;
        #endregion

        #region Unity/VRChat LifeCycles

        private void Start()
        {
            _seatCount = (unoPlayers != null) ? unoPlayers.Length : 0;
            _handCapacity = Mathf.Max(1, MaxCardsPerHand);

            EnsureArrays();

            _seatCardBuffer = new int[_handCapacity];
            _cardUsed = new bool[(unoCards != null) ? unoCards.Length : 0];

            _relayLastSeq = new int[_seatCount];
            for (int i = 0; i < _relayLastSeq.Length; i++) _relayLastSeq[i] = -1;

            // Bind manager + ids on cards
            if (unoCards != null)
            {
                for (int i = 0; i < unoCards.Length; i++)
                {
                    UnoCard c = unoCards[i];
                    if (c == null) continue;

                    c.Manager = this;
                    c.CardID = i;

                    if (c.CardRenderer == null)
                        c.CardRenderer = c.GetComponent<MeshRenderer>();
                }
            }

            if (Networking.IsMaster)
            {
                Networking.SetOwner(Networking.LocalPlayer, gameObject);

                InitDeckOrder();
                ResetAllCardsToDeckMaster();

                _matchStarted = false;
                _lastPlayedCardIndex = -1;

                _stateSeq++;
                RequestSerialization();
            }

            ApplyStateToScene(force: true);
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (!Networking.IsMaster) return;
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            RequestSerialization();
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (!Networking.IsMaster) return;
            if (player == null) return;

            int pid = player.playerId;
            bool changed = false;

            for (int seat = 0; seat < _seatCount; seat++)
            {
                if (_playerIds[seat] == pid)
                {
                    ClearSeatMaster(seat);
                    changed = true;
                }
            }

            if (changed)
            {
                _stateSeq++;
                RequestSerialization();
                ApplyStateToScene(force: true);
            }
        }

        public override void OnDeserialization()
        {
            ApplyStateToScene(force: true);
        }

        private void Update()
        {
            if (Networking.IsMaster)
            {
                _masterTimer += Time.deltaTime;
                if (_masterTimer >= MasterTick)
                {
                    _masterTimer = 0f;

                    bool changed = false;
                    if (ProcessRelayRequestsMaster()) changed = true;
                    if (ProcessPlayRequestsMaster()) changed = true;

                    if (changed)
                    {
                        _stateSeq++;
                        RequestSerialization();
                        ApplyStateToScene(force: true);
                    }
                }
            }

            _clientTimer += Time.deltaTime;
            if (_clientTimer >= ClientLayoutTick)
            {
                _clientTimer = 0f;
                ApplyStateToScene(force: false);
            }
        }

        #endregion

        #region Helpers

        public void ResortHandsLocal()
        {
            ApplyStateToScene(force: true);
        }

        public bool CanLocalPlayCard(int cardId)
        {
            if (!_matchStarted) return false;
            if (Networking.LocalPlayer == null) return false;

            int seat = FindSeatOfPlayer(Networking.LocalPlayer.playerId);
            if (seat < 0) return false;

            return FindCardSlotInSeat(seat, cardId) >= 0;
        }

        public void ProcessRelayRequestsOwner()
        {
            if (!Networking.IsMaster) return;

            bool changed = ProcessRelayRequestsMaster();
            if (changed)
            {
                _stateSeq++;
                RequestSerialization();
            }

            ApplyStateToScene(force: true);
        }

        private void ReinsertCardIntoDeck(int cardIndex)
        {
            if (_deckOrder == null || _deckOrder.Length == 0)
                return;

            // Prevent duplicates
            for (int i = _deckTop; i < _deckOrder.Length; i++)
            {
                if (_deckOrder[i] == cardIndex)
                    return;
            }

            // Reopen one slot in the available range
            if (_deckTop > 0)
                _deckTop--;

            // Pick a random position in the remaining deck
            int insertPos = Random.Range(_deckTop, _deckOrder.Length);

            // Swap into that position
            int temp = _deckOrder[insertPos];
            _deckOrder[insertPos] = cardIndex;
            _deckOrder[_deckTop] = temp;
        }



        #endregion

        #region Match Control

        public void StartMatchButton()
        {
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(StartMatchOwner));
        }

        public void StartMatchOwner()
        {
            if (!Networking.IsMaster) return;
            if (_matchStarted) return;

            _matchStarted = true;

            // Deal starting hands
            for (int seat = 0; seat < _seatCount; seat++)
            {
                if (_playerIds[seat] == -1) continue;
                for (int i = 0; i < StartingHandSize; i++)
                    DrawCardToSeatMaster(seat);
            }

            // Ensure initial played card exists
            if (_lastPlayedCardIndex == -1)
            {
                int first = DrawTopCardMaster();
                if (first >= 0 && unoCards != null && first < unoCards.Length)
                {
                    UnoCard c = unoCards[first];
                    if (c != null)
                    {
                        Networking.SetOwner(Networking.LocalPlayer, c.gameObject);

                        c.PlayRequested = false;
                        c.IsInPlayedSlot = true;

                        if (LastPlayedCardTransform != null)
                            c.transform.SetPositionAndRotation(
                                LastPlayedCardTransform.position,
                                LastPlayedCardTransform.rotation
                            );

                        _lastPlayedCardIndex = first;
                    }
                }
            }

            _stateSeq++;
            RequestSerialization();
            ApplyStateToScene(force: true);
        }

        public void ResetMatchButton()
        {
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ResetMatchOwner));
        }

        public void ResetMatchOwner()
        {
            if (!Networking.IsMaster) return;

            // Clear all seats
            for (int seat = 0; seat < _seatCount; seat++)
                _playerIds[seat] = -1;

            // Reset match state and return all cards to deck
            _matchStarted = false;
            InitDeckOrder();
            ResetAllCardsToDeckMaster();

            _stateSeq++;
            RequestSerialization();
            ApplyStateToScene(force: true);
        }

        #endregion

        #region Arrays

        private void EnsureArrays()
        {
            if (_seatCount <= 0) return;

            int handLen = _seatCount * _handCapacity;

            if (_playerIds == null || _playerIds.Length != _seatCount)
            {
                _playerIds = new int[_seatCount];
                for (int i = 0; i < _playerIds.Length; i++) _playerIds[i] = -1;
            }

            if (_handCardIndex == null || _handCardIndex.Length != handLen)
            {
                _handCardIndex = new int[handLen];
                for (int i = 0; i < _handCardIndex.Length; i++) _handCardIndex[i] = -1;
            }
        }

        private int HandIndex(int seat, int slot) => seat * _handCapacity + slot;

        #endregion

        #region Deck/Draw

        private void InitDeckOrder()
        {
            int n = (unoCards != null) ? unoCards.Length : 0;
            _deckOrder = new int[n];
            for (int i = 0; i < n; i++) _deckOrder[i] = i;

            for (int i = n - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                int t = _deckOrder[i];
                _deckOrder[i] = _deckOrder[j];
                _deckOrder[j] = t;
            }

            _deckTop = 0;
        }

        private void ResetAllCardsToDeckMaster()
        {
            if (!Networking.IsMaster) return;
            if (unoCards == null || DeckTransform == null) return;

            for (int i = 0; i < _handCardIndex.Length; i++) _handCardIndex[i] = -1;
            _lastPlayedCardIndex = -1;

            for (int i = 0; i < unoCards.Length; i++)
            {
                UnoCard c = unoCards[i];
                if (c == null) continue;

                Networking.SetOwner(Networking.LocalPlayer, c.gameObject);

                c.IsInPlayedSlot = false;
                c.PlayRequested = false;
                c.RequestSerialization();

                c.transform.SetPositionAndRotation(DeckTransform.position, DeckTransform.rotation);
                c.transform.localScale = Vector3.one;
            }
        }

        private int DrawTopCardMaster()
        {
            // Deck not initialized
            if (_deckOrder == null)
                return -1;

            // Deck exhausted; deny draw
            if (_deckTop >= _deckOrder.Length)
                return -1;

            return _deckOrder[_deckTop++];
        }


        private int FindEmptyHandSlot(int seat)
        {
            for (int slot = 0; slot < _handCapacity; slot++)
                if (_handCardIndex[HandIndex(seat, slot)] == -1)
                    return slot;
            return -1;
        }

        private void DrawCardToSeatMaster(int seat)
        {
            if (!Networking.IsMaster) return;

            int slot = FindEmptyHandSlot(seat);
            if (slot == -1) return;

            int cardIndex = DrawTopCardMaster();
            if (cardIndex < 0) return;

            // Before inserting a drawn card
            for (int i = 0; i < _handCardIndex.Length; i++)
            {
                if (_handCardIndex[i] == cardIndex)
                    _handCardIndex[i] = -1;
            }

            _handCardIndex[HandIndex(seat, slot)] = cardIndex;

            UnoCard c = unoCards[cardIndex];
            if (c != null)
            {
                c.ResetPlayedAnimationState();
                c.LocalPendingPlay = false;
                c.IsInPlayedSlot = false;
                c.PlayRequested = false;
                c.RequestSerialization();
            }

            int pid = _playerIds[seat];
            VRCPlayerApi p = VRCPlayerApi.GetPlayerById(pid);

            if (p != null && p.IsValid())
                Networking.SetOwner(p, c.gameObject);
            else
                Networking.SetOwner(Networking.LocalPlayer, c.gameObject);
        }


        private void ClearSeatMaster(int seat)
        {
            if (!Networking.IsMaster) return;
            if (seat < 0 || seat >= _seatCount) return;

            for (int slot = 0; slot < _handCapacity; slot++)
            {
                int ci = _handCardIndex[HandIndex(seat, slot)];
                if (ci >= 0 && ci < unoCards.Length)
                    ReturnCardToDeckMaster(ci);
            }

            _playerIds[seat] = -1;
        }

        private void ReturnCardToDeckMaster(int cardIndex)
        {
            if (!Networking.IsMaster) return;
            if (unoCards == null || cardIndex < 0 || cardIndex >= unoCards.Length) return;

            UnoCard c = unoCards[cardIndex];
            if (c == null) return;

            for (int i = 0; i < _handCardIndex.Length; i++)
            {
                if (_handCardIndex[i] == cardIndex)
                    _handCardIndex[i] = -1;
            }

            ReinsertCardIntoDeck(cardIndex);

            Networking.SetOwner(Networking.LocalPlayer, c.gameObject);

            c.ResetLocalState();
            c.IsInPlayedSlot = false;
            c.PlayRequested = false;
            c.ResetPlayedAnimationState();
            c.RequestSerialization();

            if (DeckTransform != null)
                c.transform.SetPositionAndRotation(DeckTransform.position, DeckTransform.rotation);

            c.transform.localScale = Vector3.one;
        }

        #endregion

        #region Relays

        private bool ProcessRelayRequestsMaster()
        {
            bool changed = false;

            for (int seat = 0; seat < _seatCount; seat++)
            {
                UnoPlayerHand hand = unoPlayers[seat];
                if (hand == null) continue;

                UnoSeatRelay relay = hand.Relay;
                if (relay == null) continue;

                if (relay.RequestSeq == _relayLastSeq[seat]) continue;
                _relayLastSeq[seat] = relay.RequestSeq;

                int pid = relay.RequestPlayerId;
                int type = relay.RequestType; // 1 join, 2 deal
                if (pid < 0) continue;

                if (type == 1)
                {
                    if (_playerIds[seat] == -1 || _playerIds[seat] == pid)
                    {
                        _playerIds[seat] = pid;
                        changed = true;
                    }
                }
                else if (type == 2)
                {
                    if (_matchStarted && _playerIds[seat] == pid)
                    {
                        DrawCardToSeatMaster(seat);
                        changed = true;
                    }
                }
                else if (type == 3)
                {
                    if (_playerIds[seat] == pid)
                    {
                        ClearSeatMaster(seat);
                        changed = true;
                    }
                }
            }

            return changed;
        }
        #endregion

        #region Play Card Proccessing

        private bool ProcessPlayRequestsMaster()
        {
            if (!_matchStarted) return false;
            if (unoCards == null) return false;

            for (int i = 0; i < unoCards.Length; i++)
            {
                UnoCard c = unoCards[i];
                if (c == null) continue;
                if (!c.PlayRequested) continue;

                AcceptPlayedCardMaster(i);
                return true;
            }

            return false;
        }

        private void AcceptPlayedCardMaster(int newIndex)
        {
            if (!Networking.IsMaster) return;
            if (unoCards == null || newIndex < 0 || newIndex >= unoCards.Length) return;

            if (_lastPlayedCardIndex != -1 && _lastPlayedCardIndex != newIndex)
                ReturnCardToDeckMaster(_lastPlayedCardIndex);

            for (int i = 0; i < _handCardIndex.Length; i++)
            {
                if (_handCardIndex[i] == newIndex)
                    _handCardIndex[i] = -1;
            }

            UnoCard c = unoCards[newIndex];
            if (c == null) return;

            Networking.SetOwner(Networking.LocalPlayer, c.gameObject);

            c.PlayRequested = false;
            c.IsInPlayedSlot = true;
            c.RequestSerialization();

            if (LastPlayedCardTransform != null)
            {
                c.transform.SetPositionAndRotation(
                    LastPlayedCardTransform.position,
                    LastPlayedCardTransform.rotation
                );
            }

            _lastPlayedCardIndex = newIndex;
        }
        #endregion

        #region Visuals and Layout

        private void ApplyStateToScene(bool force)
        {
            EnsureArrays();

            // Seat UI
            for (int seat = 0; seat < _seatCount; seat++)
            {
                UnoPlayerHand hand = unoPlayers[seat];
                if (hand == null) continue;

                int pid = _playerIds[seat];
                if (pid == -1) hand.ClearPlayer();
                else hand.SetPlayer(VRCPlayerApi.GetPlayerById(pid));
            }

            if (unoCards == null || unoCards.Length == 0) return;

            // used map = in any hand or played
            for (int i = 0; i < _cardUsed.Length; i++)
                _cardUsed[i] = false;

            if (_lastPlayedCardIndex >= 0 && _lastPlayedCardIndex < unoCards.Length)
                _cardUsed[_lastPlayedCardIndex] = true;

            for (int i = 0; i < _handCardIndex.Length; i++)
            {
                int ci = _handCardIndex[i];
                if (ci >= 0 && ci < unoCards.Length)
                    _cardUsed[ci] = true;
            }

            // Enable / disable + HARD STATE RESET
            for (int i = 0; i < unoCards.Length; i++)
            {
                UnoCard c = unoCards[i];
                if (c == null) continue;

                bool visible = _cardUsed[i];

                // Renderer
                if (c.CardRenderer != null)
                    c.CardRenderer.enabled = visible;

                // Pickup
                var p = (VRC.SDK3.Components.VRCPickup)c.GetComponent(typeof(VRC.SDK3.Components.VRCPickup));
                if (p != null)
                {
                    p.enabled = visible;
                    p.pickupable = visible;
                }

                if (i == _lastPlayedCardIndex)
                {
                    // Actively played card
                    c.IsInPlayedSlot = true;
                    c.LocalPendingPlay = false;
                }
                else if (!c.LocalPendingPlay)
                {
                    // Not the played card, and not mid-play animation.
                    // Cards with LocalPendingPlay are flying to the played
                    // slot and haven't been confirmed by master yet — leave
                    // their IsInPlayedSlot / animation state untouched so the
                    // fly animation isn't interrupted.
                    c.IsInPlayedSlot = false;

                    // CRITICAL: clear stale played animation
                    c.ResetPlayedAnimationState();

                    c.LocalPendingPlay = false;
                }
            }

            // Hand layout
            for (int seat = 0; seat < _seatCount; seat++)
            {
                UnoPlayerHand hand = unoPlayers[seat];
                if (hand == null) continue;

                int count = 0;

                for (int slot = 0; slot < _handCapacity; slot++)
                {
                    int cardIndex = _handCardIndex[HandIndex(seat, slot)];
                    if (cardIndex < 0 || cardIndex >= unoCards.Length) continue;
                    if (cardIndex == _lastPlayedCardIndex) continue;

                    UnoCard c = unoCards[cardIndex];
                    if (c == null) continue;

                    // Do not layout cards actively being played
                    if (c.LocalPendingPlay || c.IsInPlayedSlot) continue;

                    // If held, leave it alone
                    if (c.IsHeld()) continue;

                    // DUPLICATE GUARD (CRITICAL)
                    bool alreadyQueued = false;
                    for (int k = 0; k < count; k++)
                    {
                        if (_seatCardBuffer[k] == cardIndex)
                        {
                            alreadyQueued = true;
                            break;
                        }
                    }
                    if (alreadyQueued) continue;

                    if (count < _seatCardBuffer.Length)
                        _seatCardBuffer[count++] = cardIndex;
                }

                for (int i = 0; i < count; i++)
                {
                    int ci = _seatCardBuffer[i];
                    UnoCard c = unoCards[ci];
                    if (c == null) continue;

                    Vector3 pos;
                    Quaternion rot;
                    hand.GetCardPose(i, count, out pos, out rot);

                    c.SetHandTarget(pos, rot, DealFlyDuration);
                }
            }


            // Deck placement for unused cards
            if (DeckTransform != null)
            {
                for (int i = 0; i < unoCards.Length; i++)
                {
                    if (_cardUsed[i]) continue;

                    UnoCard c = unoCards[i];
                    if (c == null) continue;
                    if (c.IsHeld()) continue;

                    c.transform.localScale = Vector3.one;
                    c.SetHandTarget(
                        DeckTransform.position,
                        DeckTransform.rotation,
                        DealFlyDuration
                    );
                }
            }

            // Played pile placement
            if (_lastPlayedCardIndex >= 0 &&
                _lastPlayedCardIndex < unoCards.Length &&
                LastPlayedCardTransform != null)
            {
                UnoCard pc = unoCards[_lastPlayedCardIndex];
                if (pc != null && !pc.IsHeld())
                {
                    pc.IsInPlayedSlot = true;
                    pc.SetPlayedTarget(
                        LastPlayedCardTransform.position,
                        LastPlayedCardTransform.rotation,
                        PlayedFlyDuration
                    );
                }
            }
        }

        #endregion

        #region Lookups

        private int FindSeatOfPlayer(int playerId)
        {
            for (int i = 0; i < _playerIds.Length; i++)
                if (_playerIds[i] == playerId)
                    return i;
            return -1;
        }

        private int FindCardSlotInSeat(int seat, int cardId)
        {
            for (int slot = 0; slot < _handCapacity; slot++)
                if (_handCardIndex[HandIndex(seat, slot)] == cardId)
                    return slot;
            return -1;
        }
        #endregion
    }

}
