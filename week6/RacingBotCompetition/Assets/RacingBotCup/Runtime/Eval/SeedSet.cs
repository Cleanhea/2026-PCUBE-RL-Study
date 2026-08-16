using System;
using Newtonsoft.Json;

namespace RacingBotCup.Eval
{
    /// <summary>
    /// The tracks a submission is scored on.
    ///
    /// These seeds are public and fixed for the whole event, which means a competitor can train
    /// directly on the tracks they will be scored on. That trade is deliberate: local scoring makes
    /// hidden seeds impossible to enforce anyway, so the leaderboard is treated as a practice
    /// ladder and the finals (기획서 §8) are run by the organisers on tracks nobody has seen.
    /// A wide set makes overfitting to all of it expensive rather than impossible.
    /// </summary>
    public sealed class SeedSet
    {
        public string Id = "public-v1";
        public string Description = "RacingBot Cup public evaluation seeds";
        public int[] Seeds = Array.Empty<int>();

        public static SeedSet Default()
        {
            return new SeedSet
            {
                Id = "public-v1",
                Description = "RacingBot Cup public evaluation seeds",
                // Ten circuits, one per evaluation environment. Each produces a fully valid layout
                // within three attempts, and they spread across length (843–1986 m), section count
                // (7–13) and feature mix, so a policy has to handle short technical circuits and
                // long fast ones, with and without chicanes.
                Seeds = new[]
                {
                    3248, 3061, 3225, 3307,
                    3033, 3171, 3099, 3009,
                    3194, 3148,
                },
            };
        }

        public static bool TryParse(string json, out SeedSet seedSet, out string error)
        {
            seedSet = null;
            error = null;

            try
            {
                seedSet = JsonConvert.DeserializeObject<SeedSet>(json);
            }
            catch (JsonException exception)
            {
                error = $"eval_seeds.json could not be read: {exception.Message}";
                return false;
            }

            if (seedSet?.Seeds == null || seedSet.Seeds.Length == 0)
            {
                error = "eval_seeds.json contains no seeds.";
                return false;
            }

            return true;
        }

        public string ToJson()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }
    }
}
