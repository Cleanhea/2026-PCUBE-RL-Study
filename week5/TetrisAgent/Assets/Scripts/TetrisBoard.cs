using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Tetris
{
    /// <summary>
    /// TetrisCore 로직을 스프라이트 셀 그리드로 렌더링한다. 두 가지 모드:
    ///  - autoPlay = true : 자체 TetrisCore 를 만들어 키보드(New Input System)로 사람이 플레이(로직 검증용).
    ///  - autoPlay = false: Bind(core) 로 받은 외부 코어(예: 에이전트의 env.Core)를 렌더만 한다(학습 관전용).
    /// </summary>
    public class TetrisBoard : MonoBehaviour
    {
        [Header("블록 스프라이트 (인덱스 = PieceType: I,O,T,S,Z,J,L)")]
        public Sprite[] blockSprites = new Sprite[7];

        [Header("설정")]
        public int seed = 0;
        public float cellSize = 1f;
        public Color ghostColor = new Color(1f, 1f, 1f, 0.25f);
        public Color emptyColor = new Color(1f, 1f, 1f, 0.06f);

        [Header("모드")]
        [Tooltip("true: 자체 코어로 사람이 직접 플레이. false: Bind(core)로 받은 외부 코어를 렌더만 함(학습 관전).")]
        public bool autoPlay = true;

        [Header("입력 반복(초)")]
        public float dasDelay = 0.15f;    // 첫 반복까지 지연
        public float arrRate = 0.04f;     // 반복 간격

        TetrisCore game;
        SpriteRenderer[,] cells;          // 보드 셀
        SpriteRenderer[] nextCells;       // 넥스트 4x4 미리보기
        SpriteRenderer[] holdCells;       // 홀드 4x4 미리보기
        float spriteUnit = 1f;            // 블록 스프라이트 1칸의 월드 크기(스케일 계산용)

        // 입력 반복 타이머
        float leftT, rightT, downT;

        void Awake()
        {
            spriteUnit = blockSprites[0] != null ? blockSprites[0].bounds.size.x : 1f;
            BuildBoard();
            BuildPreview(ref nextCells, new Vector2(TetrisCore.Width + 1.5f, TetrisCore.Height - 5), "Next");
            BuildPreview(ref holdCells, new Vector2(-5.5f, TetrisCore.Height - 5), "Hold");
            if (autoPlay && game == null) game = new TetrisCore(seed);
        }

        // ML/코드에서 코어에 접근하기 위한 프로퍼티.
        public TetrisCore Game => game;

        // 외부(에이전트) 코어를 이 보드가 렌더하도록 연결한다. 이후 이 보드는 입력/중력 없이 렌더만 한다.
        // 예) 에이전트 Initialize() 안에서:  boardRef.Bind(env.Core);
        public void Bind(TetrisCore core)
        {
            game = core;
            autoPlay = false;
        }

        void BuildBoard()
        {
            cells = new SpriteRenderer[TetrisCore.Width, TetrisCore.Height];
            for (int x = 0; x < TetrisCore.Width; x++)
                for (int y = 0; y < TetrisCore.Height; y++)
                    cells[x, y] = MakeCell($"cell_{x}_{y}", CellWorld(x, y), transform);
        }

        void BuildPreview(ref SpriteRenderer[] arr, Vector2 origin, string name)
        {
            var root = new GameObject(name).transform;
            root.SetParent(transform);
            arr = new SpriteRenderer[16];
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    var pos = new Vector3(origin.x + x * cellSize, origin.y + y * cellSize, 0);
                    arr[y * 4 + x] = MakeCell($"{name}_{x}_{y}", pos, root);
                }
        }

        SpriteRenderer MakeCell(string name, Vector3 pos, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.position = pos;
            float s = cellSize / spriteUnit;
            go.transform.localScale = new Vector3(s, s, 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = blockSprites[0];
            sr.enabled = false;
            return sr;
        }

        Vector3 CellWorld(int x, int y) => new Vector3(x * cellSize, y * cellSize, 0);

        void Update()
        {
            if (game == null) return;          // autoPlay=false 이고 아직 Bind 전
            if (autoPlay)
            {
                HandleInput();
                game.Tick(Time.deltaTime);
            }
            Render();
        }

        void HandleInput()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb.rKey.wasPressedThisFrame) { game.Reset(); return; }
            if (game.GameOver) return;

            // 좌우 이동 (DAS/ARR)
            leftT = Repeat(kb.leftArrowKey, leftT, game.MoveLeft);
            rightT = Repeat(kb.rightArrowKey, rightT, game.MoveRight);

            // 소프트 드롭
            downT = Repeat(kb.downArrowKey, downT, () => game.SoftDrop());

            // 회전: 위/X = CW, Z = CCW
            if (kb.upArrowKey.wasPressedThisFrame || kb.xKey.wasPressedThisFrame) game.Rotate(1);
            if (kb.zKey.wasPressedThisFrame) game.Rotate(-1);

            // 하드 드롭
            if (kb.spaceKey.wasPressedThisFrame) game.HardDrop();

            // 홀드
            if (kb.cKey.wasPressedThisFrame || kb.leftShiftKey.wasPressedThisFrame) game.HoldPiece();
        }

        // 키를 누르는 순간 1회, 이후 dasDelay 뒤 arrRate 간격으로 반복 호출.
        float Repeat(KeyControl key, float timer, System.Func<bool> act)
        {
            if (key.wasPressedThisFrame) { act(); return dasDelay; }
            if (key.isPressed)
            {
                timer -= Time.deltaTime;
                if (timer <= 0f) { act(); return arrRate; }
                return timer;
            }
            return 0f;
        }

        void Render()
        {
            // 보드 초기화
            for (int x = 0; x < TetrisCore.Width; x++)
                for (int y = 0; y < TetrisCore.Height; y++)
                {
                    int v = game.Grid[x, y];
                    var sr = cells[x, y];
                    if (v == TetrisCore.Empty) { sr.enabled = true; sr.sprite = blockSprites[0]; sr.color = emptyColor; }
                    else { sr.enabled = true; sr.sprite = blockSprites[v]; sr.color = Color.white; }
                }

            if (!game.GameOver)
            {
                // 고스트
                foreach (var c in game.GhostCells())
                    if (InBoard(c) && game.Grid[c.x, c.y] == TetrisCore.Empty)
                    { cells[c.x, c.y].sprite = blockSprites[(int)game.Current]; cells[c.x, c.y].color = ghostColor; }
                // 현재 조각
                foreach (var c in game.CurrentCells())
                    if (InBoard(c))
                    { cells[c.x, c.y].sprite = blockSprites[(int)game.Current]; cells[c.x, c.y].color = Color.white; }
            }

            // 넥스트 / 홀드 미리보기
            DrawPreview(nextCells, game.Next.Peek());
            if (game.Hold.HasValue) DrawPreview(holdCells, game.Hold.Value);
            else ClearPreview(holdCells);
        }

        void DrawPreview(SpriteRenderer[] arr, PieceType p)
        {
            ClearPreview(arr);
            foreach (var c in game.CellsAt(p, 0, Vector2Int.zero))
            {
                int idx = c.y * 4 + c.x;
                if (idx >= 0 && idx < 16) { arr[idx].enabled = true; arr[idx].sprite = blockSprites[(int)p]; arr[idx].color = Color.white; }
            }
        }

        void ClearPreview(SpriteRenderer[] arr) { foreach (var sr in arr) sr.enabled = false; }

        bool InBoard(Vector2Int c) => c.x >= 0 && c.x < TetrisCore.Width && c.y >= 0 && c.y < TetrisCore.Height;

        // 간단 HUD(IMGUI) — 에셋/폰트 의존 없이 점수 확인용.
        // ponytail: 디버그용 IMGUI HUD. 예쁜 UI 필요하면 TMP + Canvas 로 교체.
        void OnGUI()
        {
            GUI.skin.label.fontSize = 20;
            GUI.Label(new Rect(10, 10, 300, 30), $"Score: {game.Score}");
            GUI.Label(new Rect(10, 40, 300, 30), $"Level: {game.Level}");
            GUI.Label(new Rect(10, 70, 300, 30), $"Lines: {game.Lines}");
            if (game.GameOver)
            {
                GUI.skin.label.fontSize = 32;
                GUI.Label(new Rect(10, 110, 400, 40), "GAME OVER  (R: restart)");
                GUI.skin.label.fontSize = 20;
            }
            GUI.Label(new Rect(10, Screen.height - 90, 600, 80),
                "←/→ 이동  ↓ 소프트드롭  Space 하드드롭\n↑/X 회전CW  Z 회전CCW  C/Shift 홀드  R 리셋");
        }
    }
}
