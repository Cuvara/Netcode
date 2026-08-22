using System.Collections.Generic;
using Cuvara.Netcode.Json;
using Shared.GameLogic.Content;

namespace Cuvara.Netcode.Content
{
    /// <summary>
    /// Turns the server's content document into <see cref="ItemDefinition"/> objects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the client half of a deliberate asymmetry (ADR-19): the <b>schema</b> and the
    /// <b>validator</b> come from <c>Shared.GameLogic</c> and are identical on both sides,
    /// but the <b>parser</b> is per-side. The server uses source-generated
    /// <c>System.Text.Json</c>; Unity compiles <c>Shared.GameLogic</c> as source and has no
    /// such library, so this uses the netcode package's hand-written reader instead.
    /// </para>
    /// <para>
    /// The asymmetry is forced rather than chosen, and its risk is drift between two
    /// readers of one document. That risk is contained by both sides constructing the same
    /// shared types and running the same shared validation — so a divergence shows up as a
    /// validation failure or a missing field, not as two subtly different worlds.
    /// </para>
    /// </remarks>
    public static class ContentJsonReader
    {
        /// <summary>
        /// Parses a content document. Returns false and fills <paramref name="error"/>
        /// rather than throwing, because the caller is a download path where a malformed
        /// body is an expected outcome, not an exceptional one.
        /// </summary>
        public static bool TryRead(string json, string hash, out ContentDatabase database, out string error)
        {
            database = null;
            error = null;

            JsonValue root;
            try
            {
                root = JsonParser.Parse(json);
            }
            catch (JsonParseException ex)
            {
                error = "content is not valid JSON: " + ex.Message;
                return false;
            }

            // A missing 'items' key and an explicit empty array are different statements.
            // GetArray returns empty for both, so the distinction has to be drawn here or a
            // misspelled key loads as a game with no items and nothing looks wrong.
            if (!root.TryGetMember("items", out _))
            {
                error = "content has no 'items' array. A missing key and \"items\": [] are " +
                        "different things, and only one of them is intentional.";
                return false;
            }

            var array = root.GetArray("items");
            var definitions = new List<ItemDefinition>(array.Count);

            for (int i = 0; i < array.Count; i++)
            {
                JsonValue entry = array[i];

                string id = entry.GetString("id");
                if (string.IsNullOrEmpty(id))
                {
                    error = $"items[{i}] has no 'id'.";
                    return false;
                }

                if (!TryReadSlot(entry.GetString("slot"), out ItemSlot slot))
                {
                    error = $"item '{id}': slot '{entry.GetString("slot")}' is not recognised.";
                    return false;
                }

                if (!TryReadRarity(entry.GetString("rarity"), out ItemRarity rarity))
                {
                    error = $"item '{id}': rarity '{entry.GetString("rarity")}' is not recognised.";
                    return false;
                }

                // Defaulted to 0 rather than 1 on purpose: 0 is invalid, so an item whose
                // stackMax failed to read is refused by validation below instead of
                // silently becoming a single-stack item.
                definitions.Add(new ItemDefinition(
                    id,
                    entry.GetString("name"),
                    slot,
                    rarity,
                    entry.GetInt("stackMax"),
                    entry.GetInt("attack"),
                    entry.GetInt("defense"),
                    entry.GetInt("levelRequirement")));
            }

            ContentDatabase built;
            try
            {
                built = new ContentDatabase(definitions, hash);
            }
            catch (System.ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }

            // The same validator the server ran before serving this. Not distrust of the
            // server: a truncated or half-written response is indistinguishable from a valid
            // one until something checks it, and without this the client would meet the
            // problem as a null reference several screens later.
            var errors = new List<string>();
            if (!ContentValidation.Validate(built, errors))
            {
                error = "content failed validation: " + string.Join("; ", errors);
                return false;
            }

            database = built;
            return true;
        }

        private static bool TryReadSlot(string text, out ItemSlot slot)
        {
            switch (text)
            {
                case "none": slot = ItemSlot.None; return true;
                case "weapon": slot = ItemSlot.Weapon; return true;
                case "head": slot = ItemSlot.Head; return true;
                case "chest": slot = ItemSlot.Chest; return true;
                case "legs": slot = ItemSlot.Legs; return true;
                case "trinket": slot = ItemSlot.Trinket; return true;
                default: slot = ItemSlot.None; return false;
            }
        }

        private static bool TryReadRarity(string text, out ItemRarity rarity)
        {
            switch (text)
            {
                case "common": rarity = ItemRarity.Common; return true;
                case "uncommon": rarity = ItemRarity.Uncommon; return true;
                case "rare": rarity = ItemRarity.Rare; return true;
                case "epic": rarity = ItemRarity.Epic; return true;
                case "legendary": rarity = ItemRarity.Legendary; return true;
                default: rarity = ItemRarity.Common; return false;
            }
        }
    }
}
