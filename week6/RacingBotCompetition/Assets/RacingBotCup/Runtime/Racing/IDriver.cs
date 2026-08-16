namespace RacingBotCup.Racing
{
    /// <summary>
    /// Anything that can drive the car around a lap. The evaluation harness treats the rule-based
    /// baseline and a trained policy identically, which is what makes the two timed runs behind a
    /// single score directly comparable.
    ///
    /// Drivers do not tick themselves. <c>EvaluationRunner</c> calls <see cref="Tick"/> at a fixed
    /// point in each physics step; relying on MonoBehaviour update order instead would leave the
    /// result dependent on component ordering.
    /// </summary>
    public interface IDriver
    {
        string DriverName { get; }

        void Bind(RaceContext context);

        void BeginRun();

        /// <summary>Reads the context and pushes a fresh input into the car. Once per physics step.</summary>
        void Tick();

        void EndRun();
    }
}
