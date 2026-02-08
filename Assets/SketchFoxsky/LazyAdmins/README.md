# Lazy Admins
Single script solution to add and remove admins to your world using a site like Pastebin!

## Documentation

### LazyAdminManager

#### Properties
- **Admin List** (VRCURL)
  - This is the URL containing the Names of players you wish to be admins. Pastebin is a good site for this.
- **Admin GameObjects[]**
  - These are the items to enable for the Admins, they will be disabled for non admins.
- **Tick Rate**
  - Time in Seconds to refresh the list.
- **Enable Lazy Password**
  - When enabled, this script will check the first entry on the list to match the LazyPassword defined.
- **Lazy Password**
  - This is the password the script will check against, this should be the first entry on the list however it can be anywhere on the list if you're paranoid someone has your list.
  - Please don't use an ACTUAL password you use.

#### Debug Info
This is information the script assigns itself, do not edit this in unity!
- **Lazy List Raw**
  - The raw full list loaded from the URL.
- **Lazy Admin Names[]**
  - Loaded and seperated entries from the URL
- **IsAdmin**
  - Are you an admin?
- **LocalPlayerName**
  - The LocalPlayer name.

## Setup

### LazyAdminManager

1. Using a site like Pastebin make a list of names.
   - Names are case and space sensitive, `SkeTch foxSky` will not work for the user `SketchFoxsky`.
   - Usernames like `༒ĐɆⱠɆ₮łØ₦༒` will need to be Copy/Pasted from the VRChat website as VRChat sanitizes these names to render correctly on Nameplates.
   - If you are using Lazy Password you want to include it somewhere in the list.
2. Export this list and use the RAW link.
   - We want `https://pastebin.com/raw/(yourlist)` not `https://pastebin.com/(yourlist)`.
3. Paste this link into the **Admin List** property in *LazyAdminManager*.
4. Assign the GameObjects you wish to toggle for admins in the **Admin GameObjects** property in *LazyAdminManager*
   - Optional, you can also assign the **Lazy Password** if you are using it and have **Enable Lazy Password** enabled.
5. Assign your **Tick Rate** and upload.
