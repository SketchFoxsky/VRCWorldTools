using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace SketchFoxsky.Uno
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class UnoSeatRelay : UdonSharpBehaviour
    {
        [Header("Links")]
        public UnoGameManager Manager;
        public UnoPlayerHand Hand;

        [Header("Synced Request")]
        [UdonSynced] public int RequestSeq;
        [UdonSynced] public int RequestType;     // 1 = Join, 2 = Deal
        [UdonSynced] public int RequestPlayerId; // sender

        public void RequestJoin()
        {
            SendRequest(1);
        }

        public void RequestDeal()
        {
            SendRequest(2);
        }

        public void RequestLeave()
        {
            SendRequest(3);
        }

        private void SendRequest(int type)
        {
            if (Networking.LocalPlayer == null) return;
            if (Manager == null) return;

            // Ensure THIS relay object is owned by the local player so they can serialize the request.
            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(Networking.LocalPlayer, gameObject);

            RequestType = type;
            RequestPlayerId = Networking.LocalPlayer.playerId;

            // MUST increment so master sees it as new
            RequestSeq++;

            RequestSerialization();

            // Nudge the manager owner (master) to process immediately
            Manager.SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(UnoGameManager.ProcessRelayRequestsOwner));
        }
    }
}