using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace HouseItemsFix
{
    // House Items Fix: vanilla bugs that lose items kept in a bought house, and their ship counterpart (HOUSE_ITEMS_FIX_DRAFT.md
    // for 1 and 2, POCKET_AND_SHIP_CRATE_DRAFT.md for 3 and 4; the evidence in TEST_RESULTS.md). All the mod's code is in this file.
    //  1. The house's inside box (its House-tagged BoxCollider) reaches past the hut onto the ground, which lies under the box's
    //     lower edge in places at houses 204, 202 and 201 (the doorway, beside and under the hut; the huts' wooden floors are
    //     inside). A small item lying there, made again after a trip or a load, never meets the box, belongs to no Transform and is
    //     left behind by a 512 m world shift. Each box is lowered to just under that ground; the game's own ShipItem.EnterHouse
    //     then keeps the item.
    //  2. An unsealed crate's contents never become their owner's (layer 26 meets neither a house's box nor a ship's embark
    //     collider), so a house or a ship packs the crate without them. When an owner packs or sinks and when the game saves, the
    //     contents of a crate take that crate's save parent where an owner is involved; when a crate leaves a house's box or a ship
    //     they let go of it with the crate. The game's own packing and its crate re-insert do the rest. (4 widened this from houses
    //     to ships.)
    //  3. An item pocketed inside a house's box stays the house's (the pocket turns its collider off, so no exit from the box
    //     follows), and the house packs it out of the pocket 1 km off. It lets go of the house as it goes into the pocket, as the
    //     game already lets a ship's item go (ShipItem.OnEnterInventory calls ExitBoat).
    [BepInPlugin(Guid, "House Items Fix", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "DogEggz.HouseItemsFix", Version = "1.0.0";
        internal static ManualLogSource Log;
        internal static Plugin Instance;

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            // Houses already awake when the plugin loads (the postfix on PurchasableBoat.Awake catches the rest).
            int lowered = 0;
            foreach (var house in Resources.FindObjectsOfTypeAll<PurchasableBoat>())
                if (house && house.gameObject.scene.IsValid() && HouseBoxes.Lower(house)) lowered++;
            Log.LogInfo("House Items Fix " + Version + " loaded" + (lowered > 0 ? "; lowered " + lowered + " house boxes" : ""));
        }
    }

    // ---- 1. The inside box reaches below the ground in it ----
    internal static class HouseBoxes
    {
        // The new lower edge of each house's box, in the house's frame: 0.2 m under the lowest ground read in game inside the box
        // (hq-1, and EconomyOverhaul's hf-1; TEST_RESULTS.md). A box already lower is left alone, so 203 and 206 aren't listed.
        // shortcut: values measured on Sailwind 0.40.1's scene; a game update that moves or reshapes a house needs them measured
        // again (HouseItemsFix.QA's box and "newly inside" readings) before the next release.
        private static readonly Dictionary<int, float> LowerEdge = new Dictionary<int, float>
        {
            { 204, -2.68f },   // Serpent Isle: edge -2.24, ground down to -2.48
            { 202, -2.76f },   // Dragon Cliffs: edge -2.24, ground down to -2.56
            { 201, -3.67f },   // Gold Rock City: edge -3.17, ground down to -3.47
            { 205, -3.72f },   // Oasis: edge -3.17, ground down to -3.10; Oasis House Fix stretches this box over its yard, whose
                               // margins go down to -3.52 (hq-13); nothing lies under -3.17 in the vanilla box (hq-4, hq-12)
        };

        // Moves the box's lower edge down to the table's height, keeping its top and footprint. True if it changed the box.
        internal static bool Lower(PurchasableBoat house)
        {
            try
            {
                if (!house || !house.isHouse) return false;
                var saveable = house.GetComponent<SaveableObject>();
                if (!saveable || !LowerEdge.TryGetValue(saveable.sceneIndex, out float edge)) return false;
                BoxCollider box = null;
                foreach (var c in house.GetComponentsInChildren<BoxCollider>(true))
                    if (c.isTrigger && c.CompareTag("House")) { box = c; break; }
                if (!box) { Plugin.Log.LogWarning("House " + saveable.sceneIndex + ": no House box found; left as the game has it."); return false; }
                // The edge in the box's own frame (the box sits on the house root in 0.40.1; a child box is converted the same way).
                float target = box.transform.InverseTransformPoint(house.transform.TransformPoint(new Vector3(0f, edge, 0f))).y;
                float bottom = box.center.y - box.size.y / 2f, top = box.center.y + box.size.y / 2f;
                if (target >= bottom - 0.001f) return false;   // already low enough (or lowered before)
                box.size = new Vector3(box.size.x, top - target, box.size.z);
                box.center = new Vector3(box.center.x, (top + target) / 2f, box.center.z);
                Plugin.Log.LogInfo("House " + saveable.sceneIndex + ": its inside box's lower edge moved from " + bottom.ToString("F2") + " to " + target.ToString("F2") + " (box frame).");
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Lowering a house box failed (" + e.Message + "); that house is as in vanilla."); return false; }
        }
    }

    [HarmonyPatch(typeof(PurchasableBoat), "Awake")]
    internal static class HouseBoxOnAwake
    {
        private static void Postfix(PurchasableBoat __instance) => HouseBoxes.Lower(__instance);
    }

    // ---- 2 and 4. A crate's contents go where their crate does ----
    internal static class CrateContents
    {
        // The live items by save id, built once a frame (a save calls PrepareSaveData for every item in one go).
        private static readonly Dictionary<int, SaveablePrefab> byId = new Dictionary<int, SaveablePrefab>();
        private static int builtFrame = -1;

        private static SaveablePrefab Live(int instanceId)
        {
            if (builtFrame != Time.frameCount)
            {
                byId.Clear();
                foreach (var p in SaveLoadManager.instance.GetCurrentPrefabs())
                    if (p && p.instanceId != 0) byId[p.instanceId] = p;
                builtFrame = Time.frameCount;
            }
            return byId.TryGetValue(instanceId, out var found) && found ? found : null;
        }

        private static SaveableObject Owner(int sceneIndex)
        {
            if (sceneIndex <= 0) return null;
            try { var owner = SaveLoadManager.instance.GetObject(sceneIndex); return owner ? owner : null; }
            catch (IndexOutOfRangeException) { return null; }
        }

        internal static bool IsHouse(int sceneIndex)
        {
            var owner = Owner(sceneIndex);
            var boat = owner ? owner.GetComponent<PurchasableBoat>() : null;
            return boat && boat.isHouse;
        }

        // An owner: a save index whose object packs its items (BoatLocalItems): the houses and the ships, the ships for sale
        // included, as the game packs any item aboard them.
        internal static bool IsOwner(int sceneIndex)
        {
            var owner = Owner(sceneIndex);
            return owner && (owner.localItems || owner.GetComponent<BoatLocalItems>());
        }

        // An item in a crate takes its crate's save parent when the crate or the item belongs to an owner: kept with the crate when
        // the owner packs, sinks or the game saves, and let go of the owner when its crate has left it. Loose crates are as in vanilla.
        internal static void FollowCrate(SaveablePrefab item)
        {
            if (!item || item.currentCrateId == 0) return;
            var crate = Live(item.currentCrateId);
            if (!crate || crate == item) return;
            int crateParent = crate.GetParentObject(), itemParent = item.GetParentObject();
            if (crateParent == itemParent) return;
            // -2 and -3 are marks, not owners, and are never handed on either way: the packing loop marks the crate -2 ("packed")
            // before it reaches the crate's items, and sinking marks -3 (following them packed the items with no owner and a world
            // position, hq-3, FAILED file); an item marked -2 is to be destroyed by the game (ShipItem.ProcessSaveable), and giving it
            // an owner again would leave a live copy beside the packed one.
            if (crateParent == -2 || crateParent == -3 || itemParent == -2 || itemParent == -3) return;
            // An item aboard a ship on its own (put into the crate from her deck, so it boarded by her embark collider) is left to the
            // game, which keeps it in her BoatMass and under her Transform until its own ExitBoat (NAND Fixes calls it 0.2 s after
            // the crate leaves her). POCKET_AND_SHIP_CRATE_DRAFT.md, fix 4.
            var shipItem = item.GetComponent<ShipItem>();
            if (shipItem && shipItem.currentActualBoat) return;
            if (IsOwner(crateParent) || IsOwner(itemParent)) item.SetParentObject(crateParent);
        }

        // Every crate item at once, before an owner packs or sinks.
        internal static void FollowAll(string when)
        {
            try
            {
                foreach (var p in SaveLoadManager.instance.GetCurrentPrefabs())
                    if (p && p.currentCrateId != 0) FollowCrate(p);
            }
            catch (Exception e) { Plugin.Log.LogWarning(when + ": keeping crate contents with their crate failed (" + e.Message + "); they go as in vanilla."); }
        }
    }

    // When an owner packs its items (at 1 km, BoatLocalItems.CacheCurrentItems picks those whose save parent is the owner): its crates'
    // contents first take the crate's save parent, so the game packs them with their crate and puts them back in it on return.
    [HarmonyPatch(typeof(BoatLocalItems), "CacheCurrentItems")]
    internal static class PackCrateContents
    {
        private static void Prefix() => CrateContents.FollowAll("Packing");
    }

    // When a ship sinks (BoatLocalItems.CacheItemsOnSinking packs, or loses, the items whose save parent is hers): the same, so her
    // crates' contents sink and come back with her cargo. First of every prefix there: EconomyOverhaul's two prefixes list the items
    // whose save parent is hers (its rental's items, its financed cargo), and a crate item they missed would be a -3 copy they don't
    // know. The priority is on the method: HarmonyX drops a class-level one.
    [HarmonyPatch(typeof(BoatLocalItems), nameof(BoatLocalItems.CacheItemsOnSinking))]
    internal static class SinkCrateContents
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix() => CrateContents.FollowAll("Sinking");
    }

    // When an item's save data is made (a save, or a pack): an item in a crate of an owner is saved with the owner, so a load puts it
    // in the owner's pack beside its crate rather than making it at once, before the crate.
    [HarmonyPatch(typeof(SaveablePrefab), nameof(SaveablePrefab.PrepareSaveData))]
    internal static class SaveCrateContents
    {
        private static void Prefix(SaveablePrefab __instance)
        {
            try { if (__instance.currentCrateId != 0) CrateContents.FollowCrate(__instance); }
            catch (Exception e) { Plugin.Log.LogWarning("Saving a crate item with its owner failed (" + e.Message + ")."); }
        }
    }

    // When a carried crate leaves a house's box, the game lets go of the crate (ShipItem.ExitHouse: save parent -1). Its contents that
    // are the house's let go with it at once, not at the next pack or save: an item taken out of the crate before then (on a dock,
    // say) would still be the house's, and the house would pack it out of the player's pocket when they sail 1 km off.
    [HarmonyPatch(typeof(ShipItem), "ExitHouse")]
    internal static class CrateLeavesHouse
    {
        private static void Postfix(ShipItem __instance, Collider house)
        {
            try
            {
                var inventory = __instance.GetComponent<CrateInventory>();   // every crate has one; a sealed crate's is empty
                if (!inventory || inventory.containedItems.Count == 0 || !house) return;
                var owner = house.GetComponent<SaveableObject>();
                var crate = __instance.GetComponent<SaveablePrefab>();
                if (!owner || !crate) return;
                int houseIndex = owner.sceneIndex, crateParent = crate.GetParentObject();
                if (crateParent == houseIndex) return;   // still the house's: not carried (ExitHouse acts only on a held item)
                foreach (var item in inventory.containedItems)
                {
                    var saveable = item ? item.GetComponent<SaveablePrefab>() : null;
                    if (saveable && saveable.GetParentObject() == houseIndex) saveable.SetParentObject(crateParent);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Letting a crate's contents leave the house with it failed (" + e.Message + ")."); }
        }
    }

    // When a crate leaves a ship (ShipItem.ExitBoat: save parent -1, out of her embark collider), its contents that were hers let go
    // of her with it, as for a house above. Only from a real owner: DestroyItem calls ExitBoat for a crate the ship has just packed
    // (-2), and ExitBoat then gives it -1, while its packed items must keep their -2 for the game to destroy them; ExitBoat leaves -3
    // alone. Items aboard on their own are left to the game (CrateContents.FollowCrate says why).
    [HarmonyPatch(typeof(ShipItem), "ExitBoat")]
    internal static class CrateLeavesShip
    {
        private static void Prefix(ShipItem __instance, out int __state)
        {
            __state = 0;
            try
            {
                var inventory = __instance.GetComponent<CrateInventory>();
                var crate = __instance.GetComponent<SaveablePrefab>();
                if (inventory && inventory.containedItems.Count > 0 && crate) __state = crate.GetParentObject();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Reading a crate leaving a ship failed (" + e.Message + ")."); }
        }

        private static void Postfix(ShipItem __instance, int __state)
        {
            if (__state <= 0) return;   // not a crate holding items, or its save parent was none or a mark
            try
            {
                var inventory = __instance.GetComponent<CrateInventory>();
                var crate = __instance.GetComponent<SaveablePrefab>();
                if (!inventory || !crate) return;
                int now = crate.GetParentObject();
                if (now == __state) return;
                foreach (var item in inventory.containedItems)
                {
                    var saveable = item ? item.GetComponent<SaveablePrefab>() : null;
                    if (saveable && saveable.GetParentObject() == __state && !item.currentActualBoat) saveable.SetParentObject(now);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Letting a crate's contents leave the ship with it failed (" + e.Message + ")."); }
        }
    }

    // ---- 3. A pocketed item lets go of its house ----
    // Every way into a pocket runs ItemRigidbody.EnterInventorySlot, which calls OnEnterInventory: the pocket click, the reload of a
    // packed pocket item (SaveablePrefab.PutInInventory), and a port's cargo cart. Every subclass override calls this base method.
    // An item whose save parent is a house takes ShipItem.ExitHouse's two steps (ExitHouse itself acts only on a held item and needs
    // the house's collider, and a reload has neither). A ship's item is let go by the game's own ExitBoat (run hq-9). So an older
    // save's pocket item that a house holds lets go of it when the house next makes it, into the pocket.
    // That reload makes the item where the house packed it, which is where the player was then (a pocket item lies at the player),
    // often far off; it flies into its slot within 0.3 s (ItemRigidbody.LateUpdate), but the game's 600 m clean-up reads the distance
    // only every 5-8 s (ItemRigidbody.FixedUpdate, distanceCheckTimer) and destroys an item read "out of range" whose save parent is
    // 0 or below. The house's save parent spares it until that reading is renewed (vanilla), so a far one lets go 10 s of game time
    // later (run hq-10 stage 5 lost the mug letting go at once, FAILED file).
    [HarmonyPatch(typeof(ShipItem), nameof(ShipItem.OnEnterInventory))]
    internal static class PocketLeavesHouse
    {
        private const float Near = 500f, RecheckSeconds = 10f;

        private static void Postfix(ShipItem __instance)
        {
            try
            {
                var saveable = __instance.GetComponent<SaveablePrefab>();
                if (!saveable) return;
                int house = saveable.GetParentObject();
                if (!CrateContents.IsHouse(house)) return;
                var view = Camera.main;
                if (view && (view.transform.position - __instance.transform.position).sqrMagnitude > Near * Near && Plugin.Instance)
                    Plugin.Instance.StartCoroutine(LetGoLater(__instance, saveable, house));
                else LetGo(__instance, saveable, house);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Letting a pocketed item leave its house failed (" + e.Message + ")."); }
        }

        private static System.Collections.IEnumerator LetGoLater(ShipItem item, SaveablePrefab saveable, int house)
        {
            yield return new WaitForSeconds(RecheckSeconds);
            try
            {
                // Still in a pocket and still the house's (not taken out, dropped or packed again meanwhile).
                if (item && saveable && saveable.GetParentObject() == house && item.GetCurrentInventorySlot() > -1) LetGo(item, saveable, house);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Letting a pocketed item leave its house failed (" + e.Message + ")."); }
        }

        private static void LetGo(ShipItem item, SaveablePrefab saveable, int house)
        {
            saveable.SetParentObject(-1);
            if (FloatingOriginManager.instance) item.transform.parent = FloatingOriginManager.instance.transform;
            Plugin.Log.LogInfo(item.name + " is in a pocket: it is no longer house " + house + "'s.");
        }
    }
}
