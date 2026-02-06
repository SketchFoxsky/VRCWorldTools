# Setup and Documentation

This file contains documentation for the components and Setup instructions for the Uno prefab.

## Documentation

<details>
<summary>UnoGameManager</summary>
This component is the brains of the whole prefab, this component should be the root GameObject of all your Uno objects.

#### Card Pool
- **Uno Cards []**
  - The array of GameObjects with the *UnoCard* component to be used for this manager.

#### Positions
- **Deck Transform**
  - This is where all your cards will animate from when you draw a card.
  - It is recommended to keep its scale at (1,1,1).
- **Last Played Card Transform**
  - This is where played cards will fly to before being displayed for the other players.
  - You are free to scale this Object as long as it is the same value for each axis; *ex.* (4,4,4) **not** (1,5,2).

#### Player Hands
- **Uno Players []**
  - The array of GameObjects with the *UnoPlayerHand* component to be used for this manager.
  - While the game can ideally support however many you put here I built it around 8 players, you may experience some issues with more.

#### Rules
- **Max Cards Per Hand (30)**
  - The max number of cards that can be in a players hand.

#### Dealing
- **Starting Hand Size (7)**
  - How many cards are dealt to the joined players at the start of the game.
  - Mid game joining players will need to manually draw cards!

#### Visuals
- **Card Material**
  - This is the material displayed for cards visible to the player.
- **Hidden Card Material**
  - This is the material displayed for cards hidden from the player.
- **Deal Fly Duration (0.18)**
  - This is the duration for cards animation when dealt to a player.
  - You want this value above the *Client Layout Tick* and below the *Master Tick*.
  - Only change this if you know what youre doing.
- **Played Fly Duration (0.18)**
  - This is the duration for cards animation when sent to the played position.
  - You want this value above the *Client Layout Tick* and below the *Master Tick*.
  - Only change this if you know what youre doing.

#### TickRates
It is reccomended to not change these values unless you know what youre doing.

- **Client Layout Tick (0.15)**
  - This is the tick rate for each players playerhand locally when moving cards in and out of their hand.
- **Master Layout Tick (0.2)**
  - This is the tick rate for the Master to determine what cards are where.

#### Methods
These are methods used by UI buttons

- **StartMatchButton**
  - Starts the game and deals cards to joined players.
- **ResetMatchButton**
  - Returns all cards to the deck and clears all players from the PlayerHand slot.

</details>
<details>
<summary>UnoCard</summary>
  This component should be on **EVERY** uno card that will be used in the game.
  
  This component requires a *MeshRenderer*, *VRCPickup*, *Rigidbody*, and *BoxCollider* Component.

#### Refs
- **Manager**
  - The UnoGameManager this card is managed by.
- **Card Renderer**
  - The MeshRenderer for this card.
  - This component should automaticly fill this reference for you.

#### Identity
- **Card ID**
  - This is the ID of the card assigned by the *UnoGameManager* when added to the **UnoCards[]** property.

#### Ownership visuals
- **Hide From Non Owners**
  - This will dertimine if the HiddenCardMaterial will be used or not if you are NOT the owner of the card.
- **Disable Pickup For Non Owners**
  - This will determine if the VRCPickup is or is not disable if you are NOT the owner of the card.
 
#### Play Input
- **VR Hold To Play Seconds (0.25)**
  - How long VR players need to hold the Interact/Use button while holding the card to play it.

#### Played Card Visuals
- **Played Match Target Scale**
  - Will determine if the played card should or should not match the scale of the **LastPlayedCardTarget** property from the *UnoGameManager*.
- **Played Billboard To Player**
  - Will determine if the played card should or should not always face the player.
- **Played Billboard Euler Offset (0,0,0)**
  - This is the rotation offset for the played card when billboarding if the cards FBX was exported on a different rotation.

#### Play State
This is purely a debug for when testing in editor. This is handled by the *UnoGameManager*.

- **PlayRequested**
  - When true will request the *UnoGameManager* to play this card.
  - In future versions the *UnoGameManager* may deny the play if its invalid or not the players turn.
</details>
