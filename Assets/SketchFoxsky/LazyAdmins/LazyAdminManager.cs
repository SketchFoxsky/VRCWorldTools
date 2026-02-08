using System;
using UdonSharp;
using VRC.SDK3.StringLoading;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

namespace SketchFoxsky.LazyAdmins
{
    public class LazyAdminManager : UdonSharpBehaviour
    {
        #region Properties

        [Header("Properties")]
        [Tooltip("This is the URL containing the Names of players you wish to be admins. Pastebin is a good site for this.")]
        public VRCUrl AdminList;

        [Tooltip("These are the items to enable for the Admins, they will be disabled for non admins.")]
        public GameObject[] AdminGameObjects;

        [Tooltip("Time in Seconds to refresh the list.")]
        public float TickRate = 60;

        [Tooltip("When enabled, this script will check the first entry on the list to match the LazyPassword defined.")]
        public bool EnableLazyPassword;

        [Tooltip("This is the password the script will check against, this should be the first entry on the list.")]
        public string LazyPassword;

        [Header("Debug Info")]
        [Tooltip("The raw full list loaded from the URL.")]
        public string LazyListRaw;

        [Tooltip("Loaded and seperated entries from the URL")]
        public string[] LazyAdminNames;

        [Tooltip("Are you an admin?")]
        public bool IsAdmin;

        [Tooltip("The LocalPlayer name")]
        public string localPlayerName;

        [HideInInspector] private bool passedPasswordCheck;

        #endregion

        private void Start()
        {
            localPlayerName = Networking.LocalPlayer.displayName;
            Debug.Log("The local players name is: " + localPlayerName);
            _DownloadURL();
        }

        public void _DownloadURL()
        {
            if (AdminList != null)
            {
                VRCStringDownloader.LoadUrl(AdminList, (IUdonEventReceiver)this);
                SendCustomEventDelayedSeconds(nameof(_DownloadURL), TickRate);
            }
            else
            {
                Debug.LogError("No URL is assigned");
            }
        }

        public override void OnStringLoadSuccess(IVRCStringDownload result)
        {
            LazyListRaw = result.Result;
            LazyAdminNames = LazyListRaw.Split(new string[] {"\r","\n"}, StringSplitOptions.RemoveEmptyEntries);

            IsAdmin = false;
            passedPasswordCheck = false;
            _CheckPassword();
        }

        public override void OnStringLoadError(IVRCStringDownload result)
        {
            Debug.LogError(result.Error);
        }

        public void _CheckPassword()
        {
            if (!EnableLazyPassword)
            {
                passedPasswordCheck = true;
                _CheckAdmins();
            }
            else
            {
                foreach (string pass in LazyAdminNames)
                {
                    if (pass == LazyPassword)
                    {
                        passedPasswordCheck = true;
                        _CheckAdmins();
                        break;
                    }
                }
            }
        }

        public void _CheckAdmins()
        {
            if (!passedPasswordCheck)
            return;

            foreach (string adminNames in LazyAdminNames)
            {
                if (adminNames == localPlayerName)
                {
                    IsAdmin = true;
                    break;
                }
            }

            foreach (GameObject go in AdminGameObjects)
            {
                go.SetActive(IsAdmin);
            }

        }

    }
}
