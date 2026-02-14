using TMPro;
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
        public bool TurnAndCardValidation;
        public int MaxCardsPerHand = 30;
        [Tooltip("Seconds the next player has to counter a Skip / +2 / +4 before being skipped.")]
        public float ActionCardCounterWindow = 5f;
        [Tooltip("When enabled, drawing a card does not end the turn. The player can keep drawing until they have a playable card.")]
        public bool DrawUntilPlayable;
        [Tooltip("Seconds other players have to challenge a player who forgot to call UNO.")]
        public float UnoChallengeWindow = 5f;

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

        [Header("Turn & Audio")]
        public Animator TurnOrder;
        public string AnimatorBool = "Reverse";
        public AudioClip StartingCardsDeal;
        public AudioClip CardDraw;
        public AudioClip SuccessfulPlay;
        public AudioClip FailedPlay;
        public AudioClip ActionCardCountdown;
        public AudioClip UnoCallSound;
        public AudioClip UnoChallengeSound;
        public AudioClip WinSound;
        public AudioSource AudioSource;

        [Header("Text Display")]
        public Transform TextDisplayTransform;
        public TextMeshPro TextDisplay;

        #endregion

        #region Masters Variables
        [UdonSynced] private int[] _playerIds;                      // seat -> playerId (-1 empty)
        [UdonSynced] private int[] _handCardIndex;                  // seat*cap + slot -> cardIndex (-1 empty)
        [UdonSynced] private int _lastPlayedCardIndex = -1;
        [UdonSynced] private bool _matchStarted;
        [UdonSynced] private int _stateSeq;
        [UdonSynced] private int _currentTurnSeat = -1;
        [UdonSynced] private int _turnDirection = 1;                // 1 = clockwise, -1 = counter-clockwise
        [UdonSynced] private bool _actionCardPending;               // true while waiting for counter play
        [UdonSynced] private CardNum _pendingActionType = CardNum.None;
        [UdonSynced] private string _lastPlayedPlayerName = "";
        [UdonSynced] private int _unoCalledSeat = -1;               // seat that pressed UNO button
        [UdonSynced] private bool _unoChallengeActive;              // true while challenge window is open
        [UdonSynced] private int _unoVulnerableSeat = -1;           // seat that forgot to call UNO
        [UdonSynced] private string _winnerName = "";               // non-empty while winner display is shown

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
        private float _actionCardDeadline; // master-only: Time.time when counter window expires
        private int _pendingDrawCount;     // master-only: cards the threatened player must draw (2 for +2, 4 for +4, 0 for Skip)
        private float _unoChallengeDeadline; // master-only: Time.time when UNO challenge window expires
        #endregion

        #region Unity/VRChat LifeCycles

#if UNITY_EDITOR
        public void OnValidate()
        {
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
        }
#endif

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
                    // If it was this player's turn, advance before clearing
                    if (TurnAndCardValidation && _matchStarted && seat == _currentTurnSeat)
                    {
                        _actionCardPending = false;
                        _pendingDrawCount = 0;
                        AdvanceTurnMaster();
                    }

                    // Clear UNO state if the leaving player was involved
                    if (_unoCalledSeat == seat) _unoCalledSeat = -1;
                    if (_unoVulnerableSeat == seat)
                    {
                        _unoChallengeActive = false;
                        _unoVulnerableSeat = -1;
                    }

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

                    // Action card counter window expired, draw penalty cards, then skip
                    if (_actionCardPending && Time.time >= _actionCardDeadline)
                    {
                        _actionCardPending = false;
                        _pendingActionType = CardNum.None;

                        // Auto-draw penalty cards for the threatened player
                        for (int d = 0; d < _pendingDrawCount; d++)
                        {
                            DrawCardToSeatMaster(_currentTurnSeat);
                            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayCardDrawSound));
                        }
                        _pendingDrawCount = 0;

                        AdvanceTurnMaster();
                        changed = true;
                    }

                    // UNO challenge window expired player is safe
                    if (_unoChallengeActive && Time.time >= _unoChallengeDeadline)
                    {
                        _unoChallengeActive = false;
                        _unoVulnerableSeat = -1;
                        changed = true;
                    }

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

            // Billboard the text display to the local player.
            BillboardTextDisplay();
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
            if (FindCardSlotInSeat(seat, cardId) < 0) return false;

            if (TurnAndCardValidation)
            {
                if (seat != _currentTurnSeat) return false;
                if (!IsCardValidPlay(cardId)) return false;

                // prevent local play from happening during action cards
                if (_pendingActionType != CardNum.None)
                {
                    UnoCard card = unoCards[cardId];
                    if (card == null) return false;

                    if (card.CardNumber != _pendingActionType)
                        return false;
                }
            }

            return true;
        }


        public bool IsCardValidPlay(int cardIndex)
        {
            if (unoCards == null || cardIndex < 0 || cardIndex >= unoCards.Length) return false;

            UnoCard card = unoCards[cardIndex];
            if (card == null) return false;

            // Wild cards are always valid
            if (card.CardNumber == CardNum.Wild || card.CardNumber == CardNum.WildPlusFour)
                return true;

            // No last played card, anything goes
            if (_lastPlayedCardIndex < 0 || _lastPlayedCardIndex >= unoCards.Length) return true;

            UnoCard lastCard = unoCards[_lastPlayedCardIndex];
            if (lastCard == null) return true;

            // Wild on top, any card can follow
            if (lastCard.CardNumber == CardNum.Wild || lastCard.CardNumber == CardNum.WildPlusFour)
                return true;

            // Match color
            if (card.CardColor == lastCard.CardColor) return true;

            // Match number / type
            if (card.CardNumber == lastCard.CardNumber) return true;

            return false;
        }

        public bool IsLocalPlayersTurn()
        {
            if (Networking.LocalPlayer == null) return false;
            int seat = FindSeatOfPlayer(Networking.LocalPlayer.playerId);
            return seat >= 0 && seat == _currentTurnSeat;
        }

        public int GetCurrentTurnSeat()
        {
            return _currentTurnSeat;
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

        private int FindNextOccupiedSeat(int fromSeat)
        {
            for (int i = 1; i <= _seatCount; i++)
            {
                int seat = ((fromSeat + i * _turnDirection) % _seatCount + _seatCount) % _seatCount;
                if (_playerIds[seat] != -1) return seat;
            }
            return -1;
        }

        private void AdvanceTurnMaster()
        {
            if (_currentTurnSeat < 0) return;
            int next = FindNextOccupiedSeat(_currentTurnSeat);
            if (next >= 0) _currentTurnSeat = next;
        }

        private bool IsActionCard(CardNum num)
        {
            return num == CardNum.Skip || num == CardNum.PlusTwo || num == CardNum.WildPlusFour;
        }

        private int CountCardsInSeat(int seat)
        {
            int count = 0;
            for (int slot = 0; slot < _handCapacity; slot++)
            {
                if (_handCardIndex[HandIndex(seat, slot)] != -1)
                    count++;
            }
            return count;
        }

        private void BillboardTextDisplay()
        {
            if (TextDisplayTransform == null || Networking.LocalPlayer == null) return;

            var head = Networking.LocalPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            Vector3 toHead = head.position - TextDisplayTransform.transform.position;
            toHead.y = 0f;

            if (toHead.sqrMagnitude > 0.0001f)
            {
                Quaternion look = Quaternion.LookRotation(toHead.normalized, Vector3.up);
                TextDisplayTransform.transform.rotation = look;
            }
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
            _turnDirection = 1;
            _actionCardPending = false;
            _pendingDrawCount = 0;
            _lastPlayedPlayerName = "";
            _unoCalledSeat = -1;
            _unoChallengeActive = false;
            _unoVulnerableSeat = -1;
            _winnerName = "";
            _pendingActionType = CardNum.None;


            // Reset turn indicator to clockwise
            if (TurnOrder != null)
                TurnOrder.SetBool(AnimatorBool, false);

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

                        // If the first pile card is a Reverse, start counter-clockwise
                        if (TurnAndCardValidation && c.CardNumber == CardNum.Reverse)
                        {
                            _turnDirection = -1;
                            if (TurnOrder != null)
                                TurnOrder.SetBool(AnimatorBool, true);
                        }
                    }
                }
            }

            // Set initial turn to a random occupied seat
            if (TurnAndCardValidation)
            {
                int occupiedCount = 0;
                for (int seat = 0; seat < _seatCount; seat++)
                {
                    if (_playerIds[seat] != -1)
                        occupiedCount++;
                }

                if (occupiedCount > 0)
                {
                    int pick = Random.Range(0, occupiedCount);
                    int idx = 0;
                    _currentTurnSeat = -1;
                    for (int seat = 0; seat < _seatCount; seat++)
                    {
                        if (_playerIds[seat] != -1)
                        {
                            if (idx == pick)
                            {
                                _currentTurnSeat = seat;
                                break;
                            }
                            idx++;
                        }
                    }
                }
                else
                {
                    _currentTurnSeat = -1;
                }
            }

            _stateSeq++;
            RequestSerialization();
            ApplyStateToScene(force: true);

            // Starting deal sound for all clients
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayStartingDealSound));
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
            _currentTurnSeat = -1;
            _turnDirection = 1;
            _actionCardPending = false;
            _pendingDrawCount = 0;
            _lastPlayedPlayerName = "";
            _unoCalledSeat = -1;
            _unoChallengeActive = false;
            _unoVulnerableSeat = -1;
            _winnerName = "";
            _pendingActionType = CardNum.None;
            InitDeckOrder();
            ResetAllCardsToDeckMaster();

            // Reset turn indicator
            if (TurnOrder != null)
                TurnOrder.SetBool(AnimatorBool, false);

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
                c.CardState = CardState.InDeck;
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
                        bool isNewJoin = _playerIds[seat] == -1;
                        _playerIds[seat] = pid;
                        changed = true;

                        // Deal starting hand if joining mid-game
                        if (isNewJoin && _matchStarted)
                        {
                            for (int d = 0; d < StartingHandSize; d++)
                                DrawCardToSeatMaster(seat);

                            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayStartingDealSound));
                        }
                    }
                }
                else if (type == 2)
                {
                    if (_matchStarted && _playerIds[seat] == pid)
                    {
                        // When validation is on, only allow drawing on your turn
                        if (TurnAndCardValidation && seat != _currentTurnSeat)
                            continue;

                        DrawCardToSeatMaster(seat);
                        changed = true;

                        // Draw card sound for all clients
                        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayCardDrawSound));

                        if (TurnAndCardValidation)
                        {
                            // Drawing during a counter window forfeits the counter
                            if (_actionCardPending)
                            {
                                _actionCardPending = false;
                                _pendingActionType = CardNum.None;

                                // The manual draw counts as one; draw the rest of the penalty
                                for (int d = 1; d < _pendingDrawCount; d++)
                                {
                                    DrawCardToSeatMaster(seat);
                                    SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayCardDrawSound));
                                }
                                _pendingDrawCount = 0;

                                AdvanceTurnMaster();
                            }
                            else if (!DrawUntilPlayable)
                            {
                                // Drawing ends your turn
                                AdvanceTurnMaster();
                            }
                            // else: DrawUntilPlayable, player keeps their turn
                        }
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
                else if (type == 4)
                {
                    // UNO call, record that this seat called UNO
                    if (_matchStarted && _playerIds[seat] == pid)
                    {
                        _unoCalledSeat = seat;
                        changed = true;

                        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayUnoCallSound));
                    }
                }
                else if (type == 5)
                {
                    // UNO challenge, any seated player can challenge the vulnerable seat
                    if (_matchStarted && _playerIds[seat] == pid && _unoChallengeActive && seat != _unoVulnerableSeat)
                    {
                        _unoChallengeActive = false;

                        // Force the vulnerable player to draw 2 cards
                        if (_unoVulnerableSeat >= 0 && _unoVulnerableSeat < _seatCount && _playerIds[_unoVulnerableSeat] != -1)
                        {
                            DrawCardToSeatMaster(_unoVulnerableSeat);
                            DrawCardToSeatMaster(_unoVulnerableSeat);
                            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayCardDrawSound));
                        }

                        _unoVulnerableSeat = -1;
                        changed = true;

                        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayUnoChallengeSound));
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

                if (TurnAndCardValidation)
                {
                    // Find which seat owns this card
                    int seat = -1;
                    for (int s = 0; s < _seatCount; s++)
                    {
                        if (FindCardSlotInSeat(s, i) >= 0)
                        {
                            seat = s;
                            break;
                        }
                    }

                    // Deny: wrong turn or invalid card
                    if (seat < 0 || seat != _currentTurnSeat || !IsCardValidPlay(i))
                    {
                        int pid = (seat >= 0) ? _playerIds[seat] : -1;

                        Networking.SetOwner(Networking.LocalPlayer, c.gameObject);
                        c.PlayRequested = false;
                        c.IsInPlayedSlot = false;
                        c.RequestSerialization();

                        // Return ownership to the player
                        if (pid >= 0)
                        {
                            VRCPlayerApi p = VRCPlayerApi.GetPlayerById(pid);
                            if (p != null && p.IsValid())
                                Networking.SetOwner(p, c.gameObject);
                        }

                        return true;
                    }

                    // Deny: action window active but card is not a valid action card

                    if (_actionCardPending)
                    {
                        if (!IsActionCard(c.CardNumber) || c.CardNumber != _pendingActionType)
                        {
                            int pID2 = (seat >= 0) ? _playerIds[seat] : -1;

                            Networking.SetOwner(Networking.LocalPlayer, c.gameObject);
                            c.PlayRequested = false;
                            c.IsInPlayedSlot = false;
                            c.RequestSerialization();

                            if (pID2 >= 0)
                            {
                                VRCPlayerApi p2 = VRCPlayerApi.GetPlayerById(pID2);
                                if (p2 != null && p2.IsValid())
                                    Networking.SetOwner(p2, c.gameObject);
                            }

                            return true;
                        }
                    }
                }

                AcceptPlayedCardMaster(i);
                return true;
            }

            return false;
        }

        private void AcceptPlayedCardMaster(int newIndex)
        {
            if (!Networking.IsMaster) return;
            if (unoCards == null || newIndex < 0 || newIndex >= unoCards.Length) return;

            // Identify who played this card before clearing hand data
            int playedSeat = -1;
            for (int s = 0; s < _seatCount; s++)
            {
                if (FindCardSlotInSeat(s, newIndex) >= 0)
                {
                    playedSeat = s;
                    break;
                }
            }
            if (playedSeat >= 0 && _playerIds[playedSeat] >= 0)
            {
                VRCPlayerApi who = VRCPlayerApi.GetPlayerById(_playerIds[playedSeat]);
                _lastPlayedPlayerName = (who != null && who.IsValid()) ? who.displayName : "";
            }
            else
            {
                _lastPlayedPlayerName = "";
            }

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

            if (TurnAndCardValidation)
            {
                // Reverse card flips turn direction
                if (c.CardNumber == CardNum.Reverse)
                {
                    _turnDirection *= -1;
                    if (TurnOrder != null)
                        TurnOrder.SetBool(AnimatorBool, _turnDirection < 0);
                }

                if (IsActionCard(c.CardNumber))
                {
                    // Determine draw penalty for this card
                    int newDraws = 0;
                    if (c.CardNumber == CardNum.PlusTwo)
                        newDraws = 2;
                    else if (c.CardNumber == CardNum.WildPlusFour)
                        newDraws = 4;

                    // Stack draws if countering an existing action card,
                    // otherwise start fresh.
                    // e.g. +2 -> +2 = 4 draws, +4 -> +4 = 8 draws
                    if (_actionCardPending)
                    {
                        // Only stack same action types silly
                        if (c.CardNumber == _pendingActionType)
                        {
                            _pendingDrawCount += newDraws;
                        }
                    }
                    else
                    {
                        _pendingDrawCount = newDraws;
                        _pendingActionType = c.CardNumber;
                    }

                    // Advance to the next player and give them a timed
                    // window before they are skipped / forced to draw.
                    AdvanceTurnMaster();
                    _actionCardPending = true;
                    _actionCardDeadline = Time.time + ActionCardCounterWindow;

                    // Countdown sound for all clients
                    SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayActionCardCountdownSound));
                }
                else
                {
                    _actionCardPending = false;
                    _pendingActionType = CardNum.None;
                    AdvanceTurnMaster();
                }
            }

            // Win / UNO check
            if (TurnAndCardValidation && playedSeat >= 0)
            {
                int remaining = CountCardsInSeat(playedSeat);

                if (remaining == 0)
                {
                    // WINNER
                    _winnerName = _lastPlayedPlayerName;

                    // End the match but keep players seated
                    _matchStarted = false;
                    _currentTurnSeat = -1;
                    _actionCardPending = false;
                    _pendingDrawCount = 0;
                    _pendingActionType = CardNum.None;
                    _unoCalledSeat = -1;
                    _unoChallengeActive = false;
                    _unoVulnerableSeat = -1;

                    // Return all hand cards to deck (keep last played visible briefly)
                    for (int seat = 0; seat < _seatCount; seat++)
                    {
                        for (int slot = 0; slot < _handCapacity; slot++)
                        {
                            int ci = _handCardIndex[HandIndex(seat, slot)];
                            if (ci >= 0 && ci < unoCards.Length)
                                ReturnCardToDeckMaster(ci);
                        }
                    }

                    // Successful play + win sound
                    SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlaySuccessfulPlaySound));
                    SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayWinSound));
                    return;
                }

                if (remaining == 1)
                {
                    if (_unoCalledSeat == playedSeat)
                    {
                        // They called UNO
                        _unoCalledSeat = -1;
                    }
                    else
                    {
                        // Forgot to call UNO, open challenge window
                        _unoChallengeActive = true;
                        _unoVulnerableSeat = playedSeat;
                        _unoChallengeDeadline = Time.time + UnoChallengeWindow;
                    }
                }
                else
                {
                    // Not at 1 card, reset any stale UNO calls for this seat
                    if (_unoCalledSeat == playedSeat)
                        _unoCalledSeat = -1;
                }
            }

            // Successful play sound for all clients
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlaySuccessfulPlaySound));
        }
        #endregion

        #region Visuals and Layout

        private void ApplyStateToScene(bool force)
        {
            EnsureArrays();

            // Sync turn indicator animator on all clients
            if (TurnOrder != null)
                TurnOrder.SetBool(AnimatorBool, _turnDirection < 0);

            // Display the text
            if (TextDisplay != null)
            {
                if (!string.IsNullOrEmpty(_winnerName))
                {
                    // We override last played player text with winner text instead.
                    TextDisplay.text = _winnerName + "\nWins!";
                }
                else
                {
                    TextDisplay.text = _lastPlayedPlayerName;
                }
            }

            // Seat UI + UNO button visibility
            int localPid = (Networking.LocalPlayer != null) ? Networking.LocalPlayer.playerId : -1;
            for (int seat = 0; seat < _seatCount; seat++)
            {
                UnoPlayerHand hand = unoPlayers[seat];
                if (hand == null) continue;

                int pid = _playerIds[seat];
                if (pid == -1) hand.ClearPlayer();
                else hand.SetPlayer(VRCPlayerApi.GetPlayerById(pid));

                // UNO call button: show on the local player's hand when they have exactly 2 cards
                if (hand.UnoCallButtonObject != null)
                {
                    bool showUnoCall = _matchStarted && pid == localPid && pid != -1 && CountCardsInSeat(seat) == 2;
                    hand.UnoCallButtonObject.SetActive(showUnoCall);
                }

                // UNO challenge button: show for all seated players except the vulnerable one
                if (hand.UnoChallengeButtonObject != null)
                {
                    bool showChallenge = _matchStarted && _unoChallengeActive && pid == localPid && pid != -1 && seat != _unoVulnerableSeat;
                    hand.UnoChallengeButtonObject.SetActive(showChallenge);
                }
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

                if (i == _lastPlayedCardIndex)
                {
                    // Actively played card
                    c.IsInPlayedSlot = true;
                    c.LocalPendingPlay = false;
                }
                else if (c.LocalPendingPlay && !c.PlayRequested)
                {
                    // Master denied the play, reset card for everyone
                    c.IsInPlayedSlot = false;
                    c.LocalPendingPlay = false;
                    c.ResetPlayedAnimationState();
                }
                else if (!c.LocalPendingPlay)
                {
                    // Stale Animation prevention
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

        #region Audio

        public void PlaySuccessfulPlaySound()
        {
            if (AudioSource != null && SuccessfulPlay != null)
                AudioSource.PlayOneShot(SuccessfulPlay);
        }

        public void PlayFailedPlaySound()
        {
            if (AudioSource != null && FailedPlay != null)
                AudioSource.PlayOneShot(FailedPlay);
        }

        public void PlayCardDrawSound()
        {
            if (AudioSource != null && CardDraw != null)
                AudioSource.PlayOneShot(CardDraw);
        }

        public void PlayStartingDealSound()
        {
            if (AudioSource != null && StartingCardsDeal != null)
                AudioSource.PlayOneShot(StartingCardsDeal);
        }

        public void PlayActionCardCountdownSound()
        {
            if (AudioSource != null && ActionCardCountdown != null)
                AudioSource.PlayOneShot(ActionCardCountdown);
        }

        public void PlayUnoCallSound()
        {
            if (AudioSource != null && UnoCallSound != null)
                AudioSource.PlayOneShot(UnoCallSound);
        }

        public void PlayUnoChallengeSound()
        {
            if (AudioSource != null && UnoChallengeSound != null)
                AudioSource.PlayOneShot(UnoChallengeSound);
        }

        public void PlayWinSound()
        {
            if (AudioSource != null && WinSound != null)
                AudioSource.PlayOneShot(WinSound);
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
