using UnityEngine;

namespace RacingBotCup.Eval
{
    /// <summary>
    /// Where results get posted. The organisers fill this in once and ship it with the starter kit;
    /// competitors never touch it.
    ///
    /// A Google Form accepts a plain POST to its <c>formResponse</c> endpoint, which is what lets
    /// submission be a single button with no server to run. To find the field ids: open the form,
    /// View source, and search for <c>entry.</c> — each answer field has one.
    /// </summary>
    [CreateAssetMenu(menuName = "RacingBot Cup/Submission Config", fileName = "SubmissionConfig")]
    public sealed class SubmissionConfig : ScriptableObject
    {
        [Tooltip("Form response endpoint, ending in /formResponse")]
        public string FormUrl = "";

        [Tooltip("Field id for the GitHub ID answer, e.g. entry.123456789")]
        public string ParticipantEntryId = "";

        [Tooltip("Field id for the submission code answer, e.g. entry.987654321")]
        public string PayloadEntryId = "";

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(FormUrl) &&
            !string.IsNullOrWhiteSpace(ParticipantEntryId) &&
            !string.IsNullOrWhiteSpace(PayloadEntryId);

        /// <summary>
        /// Why a submission cannot be sent yet, or null when it can.
        /// </summary>
        public string Explain()
        {
            if (string.IsNullOrWhiteSpace(FormUrl))
            {
                return "Submission form URL is not set.";
            }

            if (!FormUrl.EndsWith("/formResponse", System.StringComparison.Ordinal))
            {
                return "Form URL should end in /formResponse — copy it from the form's page source, " +
                       "not from the address bar.";
            }

            if (string.IsNullOrWhiteSpace(ParticipantEntryId) || string.IsNullOrWhiteSpace(PayloadEntryId))
            {
                return "Both form field ids (entry.xxxxx) must be set.";
            }

            return null;
        }
    }
}
