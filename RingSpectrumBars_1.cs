using UnityEngine;
using UnityEngine.Rendering;

/*━━━━━━━━━
@file RingSpectrumBars.cs
@brief BGM に反応して車の周りを囲むリング状のオーディオビジュアライザー
@author 中里優太
@date 2026/09
@remarks
  ・テクスチャを使わず、FFT の結果から毎フレーム動的メッシュを生成する
  ・帯域・本数・向きを変えた 3 つのリング（高音 128 本 / 中音 96 本 / 低音 64 本）で使用
━━━━━━━━━*/

/// <summary>
/// 指定した対象（車など）を中心に、リング状のオーディオビジュアライザーを描く。
/// ワールド空間に置くので、車との前後関係は通常どおり深度で処理される。
///
/// Direction で伸びる向きを、Ribbon で「棒の集合」か「連続した帯」かを選ぶ。
/// 向きと周波数帯を変えたものを複数置くと、同じ仕組みでも別の現象に見える。
///
/// 必要なコンポーネント: MeshFilter / MeshRenderer
/// マテリアルは SG_ECGLine 製のものを複製し、
///   Rendering Pass = Default（After post-process だと深度テストされない）
///   Depth Test = LessEqual（車の裏に回った部分を隠すため）
///   Double-Sided = オン（どの角度から見ても消えないように）
/// にしたものを使う。
/// </summary>
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
[ExecuteAlways]
public class RingSpectrumBars : MonoBehaviour
{
    public enum BarDirection
    {
        Up,        // 直立。円筒の壁
        Outward,   // 地面と平行に外へ。床に広がるリング
        Inward,    // 地面と平行に中心へ。車の足元へ潜り込むリング
    }

    public enum FreqLayout
    {
        Around,             // 一周で 低音 → 高音（切れ目ができる）
        Symmetric,          // 正面が低音、背面が高音（左右対称・切れ目なし）
        SymmetricInverted,  // 正面が高音、背面が低音
    }

    [Header("中心")]
    [Tooltip("リングの中心にする対象。車の Transform を入れる。空なら自分の位置")]
    [SerializeField] private Transform target;
    [Tooltip("中心からのずらし。車の足元に合わせるなら Y を少し下げる")]
    [SerializeField] private Vector3 offset = Vector3.zero;
    [Tooltip("Up + ビルボードのときにバーを向けるカメラ。空なら Camera.main")]
    [SerializeField] private Camera targetCamera;

    [Header("リング")]
    [SerializeField] private float radius = 3.5f;
    [Range(8, 512)]
    [SerializeField] private int barCount = 96;
    [Tooltip("リングの何度ぶんを使うか。360 で全周")]
    [Range(10f, 360f)]
    [SerializeField] private float arcDegrees = 360f;
    [Tooltip("開始角度（度）")]
    [SerializeField] private float startAngle = 0f;
    [Tooltip("リングが回る速さ（度/秒）。0 で止まる")]
    [SerializeField] private float spinSpeed = 8f;

    [Header("形")]
    [Tooltip("バーが伸びる向き")]
    [SerializeField] private BarDirection direction = BarDirection.Up;
    [Tooltip("オン: 隣同士を繋いだ連続の帯にする（波形の線のような見た目）")]
    [SerializeField] private bool ribbon = false;
    [Tooltip("間隔に対するバーの太さ（1.0 で隙間なし）。Ribbon では使わない")]
    [Range(0.05f, 1f)]
    [SerializeField] private float barWidth = 0.5f;
    [Tooltip("最大の長さ（メートル）。Inward では Radius を超えないように")]
    [SerializeField] private float maxLength = 1.5f;
    [Tooltip("無音でも残る最低限の長さ")]
    [SerializeField] private float minLength = 0.05f;
    [Tooltip("床にめり込まないよう少し浮かせる")]
    [SerializeField] private float groundOffset = 0.02f;
    [Tooltip("Up のとき、オン: 面を円の外側に向ける / オフ: 常にカメラを向く")]
    [SerializeField] private bool faceOutward = true;

    [Header("周波数の並び")]
    [SerializeField] private FreqLayout freqLayout = FreqLayout.Symmetric;

    [Header("反応する周波数帯")]
    [Tooltip("このリングが見る下限の周波数 (Hz)")]
    [SerializeField] private float minHz = 40f;
    [Tooltip("このリングが見る上限の周波数 (Hz)")]
    [SerializeField] private float maxHz = 250f;
    [Tooltip("帯域の中を対数で割り当てる。人間の聴こえ方に近く、見た目も自然になる")]
    [SerializeField] private bool logWithinBand = true;
    [Tooltip("FFT のサンプル数。低音を細かく見たいほど大きくする（2の冪、256〜8192）")]
    [SerializeField] private int fftSize = 4096;

    [Header("色")]
    [ColorUsage(true, true)]
    [SerializeField] private Color barColor = Color.white;
    [Tooltip("先端をどれだけ暗くするか。1 で完全に消える")]
    [Range(0f, 1f)]
    [SerializeField] private float tipFade = 0.85f;
    [Tooltip("伸びているバーほど明るくする。0 で常に同じ明るさ")]
    [Range(0f, 1f)]
    [SerializeField] private float levelBrightness = 0.6f;

    [Header("虹色")]
    [SerializeField] private bool rainbow = false;
    [SerializeField] private float hueSpeed = 0.08f;
    [Tooltip("一周で色相が何周するか")]
    [SerializeField] private float hueSpread = 1f;
    [Range(0f, 1f)]
    [SerializeField] private float saturation = 0.8f;

    [Header("動き")]
    [Tooltip("立ち上がりの速さ。大きいほど音に鋭く反応する")]
    [SerializeField] private float smooth = 20f;
    [Tooltip("戻りの速さ。小さいほど余韻が長く残り、音楽的に見える")]
    [SerializeField] private float releaseSmooth = 6f;
    [Tooltip("隣同士をならす回数。上げるほど滑らかに繋がり、機械的な印象が減る")]
    [Range(0, 8)]
    [SerializeField] private int spatialSmooth = 2;
    [Tooltip("反応の強さ")]
    [SerializeField] private float gain = 1f;
    [SerializeField] private float dbFloor = -70f;
    [SerializeField] private float dbCeil = -12f;

    [Header("音源")]
    [Tooltip("BGM の AudioSource。空にするとゲーム全体の音に反応する")]
    [SerializeField] private AudioSource source;

    private Mesh mesh;
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;

    private float[] spectrum;
    private float[] heights;
    private float[] work;

    private Vector3[] verts;
    private Color[] colors;
    private int[] tris;
    private int cursor;

    // 1周ぶんの点を先に作ってから、棒にするか帯にするかを決める
    private Vector3[] ptBase;
    private Vector3[] ptTip;
    private Vector3[] ptSide;
    private Color[] colRoot;
    private Color[] colTip;

    private float spin;

    private void Awake() => Setup();

    private Camera ResolveCamera()
    {
        return targetCamera != null ? targetCamera : Camera.main;
    }

    private void Setup()
    {
        if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
        if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();

        barCount = Mathf.Max(2, barCount);
        fftSize = Mathf.Clamp(Mathf.ClosestPowerOfTwo(fftSize), 256, 8192);

        if (spectrum == null || spectrum.Length != fftSize)
            spectrum = new float[fftSize];

        if (heights == null || heights.Length != barCount)
        {
            heights = new float[barCount];
            work = new float[barCount];
            ptBase = new Vector3[barCount];
            ptTip = new Vector3[barCount];
            ptSide = new Vector3[barCount];
            colRoot = new Color[barCount];
            colTip = new Color[barCount];
        }

        if (verts == null || verts.Length != barCount * 4)
        {
            verts = new Vector3[barCount * 4];
            colors = new Color[barCount * 4];
            tris = new int[barCount * 6];

            for (int b = 0; b < barCount; b++)
            {
                int v = b * 4;
                int i = b * 6;
                tris[i + 0] = v + 0;
                tris[i + 1] = v + 1;
                tris[i + 2] = v + 2;
                tris[i + 3] = v + 2;
                tris[i + 4] = v + 3;
                tris[i + 5] = v + 0;
            }

            mesh = null;
        }

        if (mesh == null)
        {
            mesh = new Mesh { name = "RingSpectrumBars" };
            mesh.MarkDynamic();
            meshFilter.sharedMesh = mesh;
        }

        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = LightProbeUsage.Off;
    }

    private void LateUpdate()
    {
        Setup();

        // 中心を対象に合わせる。回転は持たせない（角度は自前で計算するため）
        Vector3 center = (target != null ? target.position : transform.position) + offset;
        transform.position = center;
        transform.rotation = Quaternion.identity;
        transform.localScale = Vector3.one;

        float dt = Application.isPlaying ? Time.deltaTime : Time.smoothDeltaTime;
        spin += spinSpeed * dt;

        if (Application.isPlaying) ReadAudio();
        else FillIdle();

        SmoothNeighbors();
        BuildMesh();
    }

    /// <summary>並び位置 u (0〜1) を、帯域内の位置 (0=下端, 1=上端) に変換する</summary>
    private float FreqAt(float u)
    {
        switch (freqLayout)
        {
            case FreqLayout.Symmetric: return 1f - Mathf.Abs(u * 2f - 1f);
            case FreqLayout.SymmetricInverted: return Mathf.Abs(u * 2f - 1f);
            default: return u;
        }
    }

    private void ReadAudio()
    {
        if (source != null) source.GetSpectrumData(spectrum, 0, FFTWindow.BlackmanHarris);
        else AudioListener.GetSpectrumData(spectrum, 0, FFTWindow.BlackmanHarris);

        float dt = Time.deltaTime;

        // spectrum 配列は 0Hz 〜 ナイキスト周波数（サンプリング周波数の半分）を
        // 均等に分割したもの。1 要素あたり何 Hz にあたるかを求めておく
        float nyquist = AudioSettings.outputSampleRate * 0.5f;
        float hzPerBin = nyquist / spectrum.Length;

        float lo = Mathf.Max(1f, Mathf.Min(minHz, maxHz));
        float hi = Mathf.Max(lo + 1f, Mathf.Max(minHz, maxHz));

        for (int i = 0; i < barCount; i++)
        {
            float u = (float)i / (barCount - 1);
            float f = FreqAt(u);

            float hz = logWithinBand
                ? lo * Mathf.Pow(hi / lo, f)
                : Mathf.Lerp(lo, hi, f);

            int idx = Mathf.Clamp(Mathf.RoundToInt(hz / hzPerBin), 0, spectrum.Length - 1);

            float db = 20f * Mathf.Log10(spectrum[idx] + 1e-7f);
            float target = Mathf.Clamp01(Mathf.InverseLerp(dbFloor, dbCeil, db)) * gain;

            // 上がるときは速く、下がるときはゆっくり
            float speed = (target > heights[i]) ? smooth : releaseSmooth;
            float k = 1f - Mathf.Exp(-Mathf.Max(0.01f, speed) * dt);

            heights[i] = Mathf.Lerp(heights[i], target, k);
        }
    }

    /// <summary>隣同士をならして、バーが独立に動いている感じを消す（円環状）</summary>
    private void SmoothNeighbors()
    {
        if (spatialSmooth <= 0) return;

        for (int pass = 0; pass < spatialSmooth; pass++)
        {
            for (int i = 0; i < barCount; i++)
            {
                int a = (i - 1 + barCount) % barCount;
                int b = (i + 1) % barCount;
                work[i] = (heights[a] + heights[i] * 2f + heights[b]) * 0.25f;
            }

            float[] tmp = heights;
            heights = work;
            work = tmp;
        }
    }

    /// <summary>編集中は音が鳴らないので、形が見えるだけの波を入れておく</summary>
    private void FillIdle()
    {
        float t = Time.realtimeSinceStartup;
        for (int i = 0; i < barCount; i++)
        {
            float u = (float)i / (barCount - 1);
            heights[i] = (Mathf.Sin((u * 8f + t * 0.5f) * Mathf.PI * 2f) * 0.5f + 0.5f) * 0.6f;
        }
    }

    private void BuildMesh()
    {
        Camera cam = ResolveCamera();

        Vector3 camLocal = cam != null
            ? transform.InverseTransformPoint(cam.transform.position)
            : new Vector3(0f, 5f, -10f);

        float hueBase = Time.realtimeSinceStartup * hueSpeed;

        bool fullCircle = arcDegrees >= 359.9f;
        float angleStep = arcDegrees / (fullCircle ? barCount : Mathf.Max(1, barCount - 1));

        float arcPerBar = Mathf.Deg2Rad * angleStep * radius;
        float halfW = arcPerBar * barWidth * 0.5f;

        // 内向きは中心を突き抜けないように制限する
        float maxLen = (direction == BarDirection.Inward)
            ? Mathf.Min(maxLength, radius * 0.95f)
            : maxLength;

        // ---- 1周ぶんの点を作る ----
        for (int i = 0; i < barCount; i++)
        {
            float u = (float)i / (barCount - 1);
            float deg = startAngle + spin + angleStep * i;
            float rad = deg * Mathf.Deg2Rad;

            Vector3 dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            Vector3 basePos = dir * radius + Vector3.up * groundOffset;

            float h = heights[i];
            float len = minLength + h * maxLen;

            // 伸びる向き
            Vector3 axis;
            switch (direction)
            {
                case BarDirection.Outward: axis = dir; break;
                case BarDirection.Inward: axis = -dir; break;
                default: axis = Vector3.up; break;
            }

            // 太さの向き（＝面の張り方）
            Vector3 side;
            if (direction == BarDirection.Up && !faceOutward)
            {
                // 板がカメラを向くように（ビルボード）
                Vector3 toCam = camLocal - basePos;
                Vector3 s = Vector3.Cross(Vector3.up, toCam);
                if (s.sqrMagnitude < 1e-6f) s = Vector3.Cross(Vector3.up, dir);
                side = s.normalized * halfW;
            }
            else
            {
                // 円の接線方向。Up なら円筒の壁、Outward/Inward なら床に寝た板になる
                side = Vector3.Cross(Vector3.up, dir).normalized * halfW;
            }

            Color c = barColor;
            if (rainbow)
            {
                float hue = Mathf.Repeat(hueBase + u * hueSpread, 1f);
                Color hsv = Color.HSVToRGB(hue, saturation, 1f);
                c = new Color(barColor.r * hsv.r, barColor.g * hsv.g, barColor.b * hsv.b, 1f);
            }

            // 伸びているところほど明るくする（加算合成なので明るさ＝濃さ）
            c *= Mathf.Lerp(1f - levelBrightness, 1f, Mathf.Clamp01(h));

            ptBase[i] = basePos;
            ptTip[i] = basePos + axis * len;
            ptSide[i] = side;

            Color cr = c; cr.a = 1f;
            Color ct = c * (1f - tipFade); ct.a = 1f;
            colRoot[i] = cr;
            colTip[i] = ct;
        }

        // ---- 点を棒または帯にする ----
        cursor = 0;

        if (ribbon)
        {
            int segs = fullCircle ? barCount : barCount - 1;
            for (int s = 0; s < segs; s++)
            {
                int a = s;
                int b = (s + 1) % barCount;
                AddQuad(ptBase[a], ptBase[b], ptTip[b], ptTip[a],
                        colRoot[a], colRoot[b], colTip[b], colTip[a]);
            }
        }
        else
        {
            for (int i = 0; i < barCount; i++)
            {
                Vector3 s = ptSide[i];
                AddQuad(ptBase[i] - s, ptBase[i] + s, ptTip[i] + s, ptTip[i] - s,
                        colRoot[i], colRoot[i], colTip[i], colTip[i]);
            }
        }

        // 使わなかった頂点は 1 点に潰して、描画されないようにする
        for (int v = cursor * 4; v < verts.Length; v++)
        {
            verts[v] = Vector3.zero;
            colors[v] = Color.clear;
        }

        mesh.Clear();
        mesh.vertices = verts;
        mesh.colors = colors;
        mesh.triangles = tris;
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (radius + maxLength) * 3f);
    }

    private void AddQuad(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3,
                         Color c0, Color c1, Color c2, Color c3)
    {
        if (cursor * 4 + 3 >= verts.Length) return;

        int v = cursor * 4;
        verts[v + 0] = v0; colors[v + 0] = c0;
        verts[v + 1] = v1; colors[v + 1] = c1;
        verts[v + 2] = v2; colors[v + 2] = c2;
        verts[v + 3] = v3; colors[v + 3] = c3;
        cursor++;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        mesh = null;
        Setup();
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0f, 1f, 1f, 0.6f);
        Vector3 c = (target != null ? target.position : transform.position) + offset;
        Vector3 prev = c + new Vector3(0f, 0f, radius);
        for (int i = 1; i <= 64; i++)
        {
            float a = i / 64f * Mathf.PI * 2f;
            Vector3 p = c + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * radius;
            Gizmos.DrawLine(prev, p);
            prev = p;
        }
    }
#endif
}