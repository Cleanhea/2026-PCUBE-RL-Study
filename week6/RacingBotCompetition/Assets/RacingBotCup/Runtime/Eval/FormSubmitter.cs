using UnityEngine;
using UnityEngine.Networking;

namespace RacingBotCup.Eval
{
    /// <summary>
    /// Posts a submission straight to the Google Form that backs the leaderboard.
    ///
    /// No server sits in between: a form's <c>formResponse</c> endpoint accepts a normal POST, so
    /// one button press is the whole submission. The form has to accept anonymous responses — with
    /// "sign-in required" turned on Google answers with a redirect to a login page and the post is
    /// silently dropped.
    /// </summary>
    public static class FormSubmitter
    {
        /// <summary>
        /// Builds the request. Send it with <c>SendWebRequest()</c> and yield on the result, or
        /// poll <c>isDone</c> if you are driving it from the editor outside play mode.
        /// </summary>
        public static UnityWebRequest Build(SubmissionConfig config, string participantId, string code)
        {
            var form = new WWWForm();
            form.AddField(config.ParticipantEntryId, participantId ?? "");
            form.AddField(config.PayloadEntryId, code ?? "");

            var request = UnityWebRequest.Post(config.FormUrl, form);
            request.timeout = 20;
            return request;
        }

        /// <summary>
        /// Reads the outcome of a finished request. Google answers a successful post with a
        /// redirect to its confirmation page, which UnityWebRequest follows and reports as 200.
        /// </summary>
        public static bool Succeeded(UnityWebRequest request, out string message)
        {
            if (request.result != UnityWebRequest.Result.Success)
            {
                message = $"Submission failed: {request.error}";
                return false;
            }

            // A form that requires sign-in bounces to accounts.google.com instead of accepting the
            // answer, which still comes back as a 200 — so the destination is what gives it away.
            if (request.url.Contains("accounts.google.com"))
            {
                message = "The form redirected to a Google sign-in page. " +
                          "Turn off \"restrict to users in your organisation\" so anonymous responses are accepted.";
                return false;
            }

            message = "Submitted.";
            return true;
        }
    }
}
