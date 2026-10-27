using System.Text;
using CivicSurvival.Core.UI.DomainState;
using CivicSurvival.Core.UI.DomainState.Attributes;

namespace EligibilityFixtures
{
    public static class Predicate
    {
        public static bool Allows() => true;
    }

    public partial struct Projection
    {
        [DtoEligibility(typeof(Predicate), nameof(Predicate.Allows), "LockedReason")]
        public bool CanStart;
        [DtoEligibility(typeof(Predicate), nameof(Predicate.Allows), "LockedReason")]
        public bool CanResume;
        [DtoEligibility(typeof(Predicate), nameof(Predicate.Allows), "ExistingReason")]
        public bool CanStop;
        public string ExistingReason;
        [DtoEligibility(typeof(Predicate), nameof(Predicate.Allows))]
        public int Ignored;

        partial void WriteEligibility(DomainJsonHelper.JsonWriter w);

        public static string Run()
        {
            var dto = new Projection { CanStart = true, CanResume = false, CanStop = true,
                LockedReason = "locked\"reason", ExistingReason = "existing", @event = true };
            var builder = new StringBuilder();
            var writer = new DomainJsonHelper.JsonWriter(builder);
            writer.Bool("before", true);
            dto.WriteEligibility(writer);
            writer.End();
            return builder.ToString();
        }
    }

    public partial struct Projection
    {
        [DtoEligibility(typeof(Predicate), nameof(Predicate.Allows))]
        public bool CanProceed;
        [DtoEligibility(typeof(Predicate), nameof(Predicate.Allows), "KeywordReason")]
        public bool @event;
    }

    public partial struct NoReason
    {
        [DtoEligibility(typeof(Predicate), nameof(Predicate.Allows))]
        public bool Enabled;
        partial void WriteEligibility(DomainJsonHelper.JsonWriter w);
        public static string Run()
        {
            var dto = new NoReason { Enabled = true };
            var builder = new StringBuilder();
            var writer = new DomainJsonHelper.JsonWriter(builder);
            dto.WriteEligibility(writer);
            writer.End();
            return builder.ToString();
        }
    }
}
