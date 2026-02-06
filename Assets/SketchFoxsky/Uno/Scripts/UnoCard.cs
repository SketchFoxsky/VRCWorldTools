using UdonSharp;
using UnityEngine;
using VRC.SDK3.Components;
using VRC.SDKBase;

namespace SketchFoxsky.Uno
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    [RequireComponent(typeof(MeshRenderer))]
    [RequireComponent(typeof(VRCPickup))]

    public class UnoCard : UdonSharpBehaviour
    {
        [Header("Refs")]
        public UnoGameManager Manager;
        public MeshRenderer CardRenderer;

        [Header("Identity")]
        [UdonSynced] public int CardID;

        [Header("Ownership visuals")]
        public bool HideFromNonOwners = true;
        public bool DisablePickupForNonOwners = true;

        [Header("Play Input")]
        public float VRHoldToPlaySeconds = 0.25f;

        [Header("Played Card Visuals")]
        public bool PlayedMatchTargetScale = true;
        public bool PlayedBillboardToPlayer = true;
        public Vector3 PlayedBillboardEulerOffset = Vector3.zero;

        [Header("Play State")]
        [UdonSynced] public bool PlayRequested;
        [HideInInspector] public bool IsInPlayedSlot;
        [HideInInspector] public bool LocalPendingPlay;

        private VRCPickup _pickup;
        private Vector3 _defaultScale;
        private bool _defaultScaleCaptured;

        // Hand fly
        private bool _handAnimating;
        private Vector3 _handStartPos;
        private Quaternion _handStartRot;
        private Vector3 _handTargetPos;
        private Quaternion _handTargetRot;
        private float _handT;
        private float _handDur;

        private Vector3 _lastHandTargetPos;
        private Quaternion _lastHandTargetRot;
        private bool _hasLastHandTarget;

        // Played fly
        private bool _playedAnimating;
        private bool _playedArrived;
        private Vector3 _playedStartPos;
        private Vector3 _playedTargetPos;
        private Vector3 _playedStartScale;
        private Vector3 _playedTargetScale;
        private float _playedT;
        private float _playedDur;

        // Input tracking
        private bool _useHeld;
        private float _useDownTime;
        private bool _playedThisHold;

        private void CacheComponentsIfNeeded()
        {
            if (CardRenderer == null) CardRenderer = (MeshRenderer)GetComponent(typeof(MeshRenderer));
            if (_pickup == null) _pickup = (VRCPickup)GetComponent(typeof(VRCPickup));

            if (!_defaultScaleCaptured)
            {
                _defaultScale = transform.localScale;
                _defaultScaleCaptured = true;
            }
        }

        private void Start()
        {
            CacheComponentsIfNeeded();
            ApplyOwnershipVisuals();
        }

        public override void OnOwnershipTransferred(VRCPlayerApi player) => ApplyOwnershipVisuals();
        public override void OnDeserialization() => ApplyOwnershipVisuals();

        private void Update()
        {
            CacheComponentsIfNeeded();
            ApplyOwnershipVisuals();

            // If it becomes played while held, force drop locally.
            if (IsInPlayedSlot && _pickup != null && _pickup.IsHeld)
                _pickup.Drop();

            // Play input while held
            if (Networking.LocalPlayer != null &&
                _pickup != null &&
                _pickup.IsHeld &&
                Networking.IsOwner(gameObject) &&
                !IsInPlayedSlot)
            {
                if (!Networking.LocalPlayer.IsUserInVR())
                {
                    if (!_playedThisHold && Input.GetKeyDown(KeyCode.E))
                    {
                        _playedThisHold = true;
                        TryRequestPlay();
                    }
                }
                else
                {
                    if (_useHeld && !_playedThisHold && (Time.time - _useDownTime) >= VRHoldToPlaySeconds)
                    {
                        _playedThisHold = true;
                        TryRequestPlay();
                    }
                }
            }

            if (_pickup != null && _pickup.IsHeld) return;

            // Played fly
            if (_playedAnimating)
            {
                _playedT += Time.deltaTime / Mathf.Max(0.01f, _playedDur);
                float u = Mathf.Clamp01(_playedT);

                transform.position = Vector3.Lerp(_playedStartPos, _playedTargetPos, u);
                transform.localScale = Vector3.Lerp(_playedStartScale, _playedTargetScale, u);

                if (u >= 1f)
                {
                    transform.position = _playedTargetPos;
                    transform.localScale = _playedTargetScale;
                    _playedAnimating = false;
                    _playedArrived = true;
                }

                // Billboard during fly so rotation is correct immediately
                ApplyPlayedBillboard();
                return;
            }

            // Hand fly
            if (_handAnimating)
            {
                _handT += Time.deltaTime / Mathf.Max(0.01f, _handDur);
                float u = Mathf.Clamp01(_handT);

                transform.SetPositionAndRotation(
                    Vector3.Lerp(_handStartPos, _handTargetPos, u),
                    Quaternion.Slerp(_handStartRot, _handTargetRot, u)
                );

                if (u >= 1f)
                {
                    transform.SetPositionAndRotation(_handTargetPos, _handTargetRot);
                    _handAnimating = false;
                }
            }

            // Billboard after played arrival
            if (IsInPlayedSlot && _playedArrived)
                ApplyPlayedBillboard();

            if (IsInPlayedSlot && _playedArrived && PlayedMatchTargetScale && Manager != null && Manager.LastPlayedCardTransform != null)
            {
                transform.localScale = Vector3.Lerp(transform.localScale, Manager.LastPlayedCardTransform.localScale, 10f * Time.deltaTime);
            }

            if (!IsInPlayedSlot && _playedAnimating)
            {
                _playedAnimating = false;
                _playedArrived = false;
            }

        }

        private void ApplyPlayedBillboard()
        {
            if (!PlayedBillboardToPlayer || Networking.LocalPlayer == null) return;

            var head = Networking.LocalPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            Vector3 toHead = head.position - transform.position;
            toHead.y = 0f;

            if (toHead.sqrMagnitude > 0.0001f)
            {
                Quaternion look = Quaternion.LookRotation(toHead.normalized, Vector3.up);
                transform.rotation = look * Quaternion.Euler(PlayedBillboardEulerOffset);
            }
        }

        private void ApplyOwnershipVisuals()
        {
            if (Manager == null || CardRenderer == null) return;

            bool isOwner = Networking.IsOwner(gameObject);

            if (HideFromNonOwners && !isOwner && !IsInPlayedSlot)
                CardRenderer.sharedMaterial = Manager.HiddenCardMaterial;
            else
                CardRenderer.sharedMaterial = Manager.CardMaterial;

            if (_pickup != null && _pickup.enabled)
            {
                if (DisablePickupForNonOwners)
                    _pickup.pickupable = isOwner && !IsInPlayedSlot;
                else
                    _pickup.pickupable = !IsInPlayedSlot;
            }
        }

        public bool IsHeld()
        {
            CacheComponentsIfNeeded();
            return _pickup != null && _pickup.IsHeld;
        }

        // Hand placement (includes the "drop return" fix)
        public void SetHandTarget(Vector3 pos, Quaternion rot, float duration)
        {
            CacheComponentsIfNeeded();

            if (_pickup != null && _pickup.IsHeld) return;
            if (IsInPlayedSlot) return;
            if (LocalPendingPlay) return; // Never hand-layout a pending-play card

            if (_hasLastHandTarget)
            {
                bool sameTarget =
                    (pos - _lastHandTargetPos).sqrMagnitude < 0.00000025f &&
                    Quaternion.Dot(rot, _lastHandTargetRot) > 0.99995f;

                if (sameTarget)
                {
                    float curDistSqr = (transform.position - pos).sqrMagnitude;
                    float curDot = Quaternion.Dot(transform.rotation, rot);
                    bool alreadyAtTarget = curDistSqr < 0.0004f && curDot > 0.999f;
                    if (alreadyAtTarget) return;
                }
            }

            _lastHandTargetPos = pos;
            _lastHandTargetRot = rot;
            _hasLastHandTarget = true;

            _handStartPos = transform.position;
            _handStartRot = transform.rotation;
            _handTargetPos = pos;
            _handTargetRot = rot;

            _handDur = Mathf.Clamp(duration, 0.05f, 1f);
            _handT = 0f;
            _handAnimating = true;
        }

        // Played placement (pos+scale), rotation handled by billboard
        public void SetPlayedTarget(Vector3 pos, Quaternion rotIgnored, float duration)
        {
            CacheComponentsIfNeeded();

            if (_pickup != null && _pickup.IsHeld)
                _pickup.Drop();

            // Don't restart if already animating to the same target
            if (_playedAnimating && (_playedTargetPos - pos).sqrMagnitude < 0.0004f)
                return;
            // Don't restart if already arrived at the target
            if (_playedArrived && (transform.position - pos).sqrMagnitude < 0.0004f)
                return;

            _playedStartPos = transform.position;
            _playedTargetPos = pos;

            _playedStartScale = transform.localScale;
            if (PlayedMatchTargetScale && Manager != null && Manager.LastPlayedCardTransform != null)
                _playedTargetScale = Manager.LastPlayedCardTransform.localScale;
            else
                _playedTargetScale = transform.localScale;

            _playedDur = Mathf.Clamp(duration, 0.05f, 1f);
            _playedT = 0f;
            _playedAnimating = true;
            _playedArrived = false;

            _handAnimating = false;
            _hasLastHandTarget = false;
        }

        public override void OnPickup()
        {
            _playedThisHold = false;
            _useHeld = false;
            if (Manager != null) Manager.ResortHandsLocal();
        }

        public override void OnDrop()
        {
            _useHeld = false;
            _playedThisHold = false;

            if (Manager != null) Manager.ResortHandsLocal();
            SendCustomEventDelayedFrames(nameof(ResortAfterDrop), 1);
        }

        public void ResortAfterDrop()
        {
            if (Manager != null) Manager.ResortHandsLocal();
        }

        public override void OnPickupUseDown()
        {
            if (_pickup == null || !_pickup.IsHeld) return;
            _useHeld = true;
            _useDownTime = Time.time;
            _playedThisHold = false;
        }

        public override void OnPickupUseUp()
        {
            _useHeld = false;
            _playedThisHold = false;
        }

        private void TryRequestPlay()
        {
            if (Manager == null) return;
            if (!Networking.IsOwner(gameObject)) return;
            if (_pickup == null || !_pickup.IsHeld) return;
            if (IsInPlayedSlot) return;
            if (!Manager.CanLocalPlayCard(CardID)) return;

            // Drop immediately
            _pickup.Drop();

            // Mark pending play (sync to master)
            PlayRequested = true;
            LocalPendingPlay = true;

            // Immediately treat as "not a hand card" locally and fly to played pile now
            IsInPlayedSlot = true;
            _handAnimating = false;
            _hasLastHandTarget = false;

            if (Manager.LastPlayedCardTransform != null)
            {
                SetPlayedTarget(
                    Manager.LastPlayedCardTransform.position,
                    Manager.LastPlayedCardTransform.rotation,
                    Manager.PlayedFlyDuration
                );
            }

            RequestSerialization();

            // Close the gap in hand right away without touching this card
            Manager.ResortHandsLocal();
        }

        public void ResetPlayedAnimationState()
        {
            _playedAnimating = false;
            _playedArrived = false;
        }

        public void ResetLocalState()
        {
            // Animation state
            _playedAnimating = false;
            _playedArrived = false;

            // Play intent
            LocalPendingPlay = false;
            IsInPlayedSlot = false;

            // Safety: hand animation should be allowed again
            _handAnimating = false;
        }


    }
}
