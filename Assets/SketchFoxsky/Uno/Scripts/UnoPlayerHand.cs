using TMPro;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SketchFoxsky.Uno
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class UnoPlayerHand : UdonSharpBehaviour
    {
        [Header("Setup")]
        public UnoGameManager Manager;
        public UnoSeatRelay Relay;
        public int SeatIndex;

        [Header("UI")]
        [HideInInspector][UdonSynced] public string playerName = "";
        public TextMeshPro PlayerName;

        [Header("Hand Root")]
        public Transform HandRoot;

        [Header("Layout")]
        public float CardSpacing = 0.015f;
        public float MaxHandWidth = 0.4f;

        [Header("Anti Z-Fight")]
        public float StepAmount = 0.0015f;

        [Header("Fan / Arc")]
        public float FanAngle = 45f;
        public float FanAngleOffset = 0f;
        public float ArcRadius = 0.2f;
        public bool UseArcPosition = true;

        [Header("Extra rotation")]
        public Vector3 CardLocalEuler = Vector3.zero;

        [HideInInspector][UdonSynced] public int PlayerId = -1;

        // Hand Axis
        private Vector3 SPREAD_AXIS = Vector3.right;    // +X
        private Vector3 STEP_AXIS = Vector3.forward;  // +Z
        private Vector3 ARC_BULGE = Vector3.down;     // -Y
        private Vector3 FAN_AXIS = Vector3.back;     // -Z


        public void SetPlayer(VRCPlayerApi player)
        {
            if (player != null)
            {
                playerName = player.displayName;
                PlayerId = player != null ? player.playerId : -1;
                PlayerName.text = playerName;
            }
            else
            {
                PlayerName.text = "";
            }

        }

        public void ClearPlayer()
        {
            PlayerId = -1;
            if (PlayerName != null) PlayerName.text = "";
            playerName = "";
        }

        // Buttons
        public void JoinGameButton()
        {
            if (Relay != null) Relay.RequestJoin();
        }

        public void DealCardButton()
        {
            if (Relay != null) Relay.RequestDeal();
        }

        public void LeaveGameButton()
        {
            if (Relay != null) Relay.RequestLeave();
        }

        public void GetCardPose(int index, int count, out Vector3 worldPos, out Quaternion worldRot)
        {
            if (HandRoot == null)
            {
                worldPos = transform.position;
                worldRot = transform.rotation;
                return;
            }

            if (count <= 0) count = 1;
            if (index < 0) index = 0;
            if (index >= count) index = count - 1;

            float effectiveSpacing = CardSpacing;
            float width = (count - 1) * effectiveSpacing;

            if (MaxHandWidth > 0f && width > MaxHandWidth)
            {
                effectiveSpacing = MaxHandWidth / Mathf.Max(1, (count - 1));
                width = (count - 1) * effectiveSpacing;
            }

            float s = (-width * 0.5f) + (index * effectiveSpacing);
            float t = (count == 1) ? 0.5f : (index / (float)(count - 1));
            float angle = (t - 0.5f) * FanAngle + FanAngleOffset;

            // StepForward = false => negative step
            float step = -(index * StepAmount);

            Vector3 localPos = SPREAD_AXIS * s + STEP_AXIS * step;

            if (UseArcPosition && ArcRadius > 0.0001f)
            {
                float rad = angle * Mathf.Deg2Rad;
                float arcSide = Mathf.Sin(rad) * ArcRadius;
                float arcBulge = (1f - Mathf.Cos(rad)) * ArcRadius;
                localPos += (SPREAD_AXIS * arcSide) + (ARC_BULGE * arcBulge);
            }

            Quaternion fanRot = Quaternion.AngleAxis(angle, FAN_AXIS);
            Quaternion extraRot = Quaternion.Euler(CardLocalEuler);

            worldPos = HandRoot.TransformPoint(localPos);
            worldRot = HandRoot.rotation * (fanRot * extraRot);
        }
    }
}
