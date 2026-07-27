using UnityEngine;

namespace Tetris
{
    /// <summary>
    /// ML 에이전트(또는 휴리스틱 봇)를 위한 테트리스 환경 래퍼.
    /// 고수준 이산 액션(열 x 회전 = 40)으로 현재 조각을 즉시 배치하고 관측/결과를 돌려준다.
    /// 프레임워크 비의존(순수 C#). ML-Agents Agent 로 쓰는 법:
    ///   - Agent 에 TetrisEnv env 필드를 둔다.
    ///   - CollectObservations: env.GetObservation() 을 sensor 에 추가.
    ///   - OnActionReceived: var r = env.Step(action); AddReward(보상); if (r.gameOver) EndEpisode();
    ///   - WriteDiscreteActionMask: !env.IsValid(action) 인 액션을 마스킹.
    ///   - OnEpisodeBegin: env.Reset();
    /// </summary>
    public class TetrisEnv
    {
        public const int Columns = TetrisCore.Width;              // 10
        public const int RotationCount = 4;
        public const int ActionCount = Columns * RotationCount;   // 40 (열 x 회전)
        public const int ObservationSize = TetrisCore.Width * TetrisCore.Height + 14; // 200 + 현재7 + 다음7

        public readonly TetrisCore Core;

        public TetrisEnv(int seed = 0) { Core = new TetrisCore(seed); }

        public void Reset() => Core.Reset();
        public bool IsDone => Core.GameOver;

        public struct StepResult
        {
            public int column, rotation, linesCleared, scoreDelta;
            public bool valid;     // 배치가 유효했는지(무효면 코어 상태는 불변)
            public bool gameOver;
        }

        // 이산 액션(0 .. ActionCount-1) 실행. rotation = action / Columns, column = action % Columns.
        public StepResult Step(int action) => Step(action % Columns, action / Columns);

        // (열, 회전) 그리드 배치 실행 후 결과 반환. 배치 불가면 valid=false, 코어 상태 불변.
        public StepResult Step(int column, int rotation)
        {
            int s0 = Core.Score, l0 = Core.Lines;
            bool valid = Core.PlacePiece(column, rotation);
            return new StepResult
            {
                column = column,
                rotation = rotation,
                valid = valid,
                linesCleared = Core.Lines - l0,
                scoreDelta = Core.Score - s0,
                gameOver = Core.GameOver,
            };
        }

        // 액션 마스킹용: 해당 위치에 배치 가능한가(상태 불변).
        public bool IsValid(int column, int rotation) => Core.CanPlace(column, rotation);
        public bool IsValid(int action) => Core.CanPlace(action % Columns, action / Columns);

        // 관측 벡터: [보드 점유 0/1 (Width*Height)] + [현재 조각 one-hot(7)] + [다음 조각 one-hot(7)].
        public float[] GetObservation()
        {
            var obs = new float[ObservationSize];
            int i = 0;
            for (int y = 0; y < TetrisCore.Height; y++)
                for (int x = 0; x < TetrisCore.Width; x++)
                    obs[i++] = Core.Grid[x, y] == TetrisCore.Empty ? 0f : 1f;
            obs[i + (int)Core.Current] = 1f; i += 7;
            obs[i + (int)Core.Next.Peek()] = 1f; i += 7;
            return obs;
        }
    }
}
