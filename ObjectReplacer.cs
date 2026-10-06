using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

/*━━━━━━━━━
@file ObjectReplacer.cs
@brief シーン内オブジェクトを指定Prefabへ一括置換するエディタ拡張
@author 中里優太
@date 2026/09/06
@remarks
  ・SpeedTree で作り直した杉を、シーンに配置済みの約450本へ差し替えるために作成
  ・元オブジェクト固有のコンポーネント設定は失われる。実行前にバックアップ推奨
  ・操作の流れ：検索 → 一覧で確認 → 確認ダイアログ → 一括置換（Ctrl+Z 1回で元に戻せる）
━━━━━━━━━*/

public class ObjectReplacer : EditorWindow
{
    /// <summary>対象の名前をどう判定するか</summary>
    public enum MatchMode
    {
        StartsWith,  // 前方一致（「Tree_001」「Tree_002」… をまとめて拾う用途）
        Contains,    // 部分一致
        ExactMatch   // 完全一致
    }

    // ---- 置き換え設定 ----
    private GameObject prefab;                       // 置き換え先の Prefab
    private string targetName = "";                  // 検索する名前
    private MatchMode matchMode = MatchMode.StartsWith;

    // ---- 絞り込み（誤検出を防ぐための条件） ----
    private bool skipPrefabInstances = true;         // 既に Prefab のものは対象外にする（置換済みを二重に置換しない）
    private bool requireComponent = false;           // 指定コンポーネントを持つものだけを対象にする
    private string requiredComponentName = "LODGroup"; // 木の親だけが持つ。子の LOD0 などを拾わないための目印

    // ---- 引き継ぐもの（Position / Rotation は常に引き継ぐ） ----
    private bool keepScale = false;
    private bool keepName = true;
    private bool keepChildren = false;

    // ---- 検索結果と表示用 ----
    private List<GameObject> found = new List<GameObject>(); // 「検索」で見つかった置換対象
    private Vector2 scroll;                                  // 一覧のスクロール位置

    /// <summary>メニュー「Tools > Object Replacer」からウィンドウを開く</summary>
    [MenuItem("Tools/Object Replacer")]
    static void Open()
    {
        GetWindow<ObjectReplacer>("Object Replacer").minSize = new Vector2(360, 460);
    }

    /// <summary>ウィンドウの描画。上から「設定 → 絞り込み → 引き継ぎ → 検索 → 一覧 → 実行」の順に並べる</summary>
    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "シーン内のオブジェクトを指定Prefabに一括置換します。\n" +
            "必ず先に「検索」で対象を確認してから実行してください。\n" +
            "実行前にプロジェクトのバックアップを推奨します。",
            MessageType.Info);

        // ---------- 置き換え設定 ----------
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("置き換え設定", EditorStyles.boldLabel);

        prefab = (GameObject)EditorGUILayout.ObjectField(
            "置き換え先Prefab", prefab, typeof(GameObject), false);
        targetName = EditorGUILayout.TextField("対象の名前", targetName);
        matchMode = (MatchMode)EditorGUILayout.EnumPopup("一致方法", matchMode);

        // ---------- 絞り込み ----------
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("絞り込み", EditorStyles.boldLabel);

        skipPrefabInstances = EditorGUILayout.Toggle(
            new GUIContent("既存Prefabを除外", "既にPrefabインスタンスのものは対象外にする"),
            skipPrefabInstances);

        requireComponent = EditorGUILayout.Toggle(
            new GUIContent("特定コンポーネント必須", "LOD0などの子オブジェクトを誤って拾わないための保険"),
            requireComponent);
        if (requireComponent)
        {
            EditorGUI.indentLevel++;
            requiredComponentName = EditorGUILayout.TextField("コンポーネント名", requiredComponentName);
            EditorGUI.indentLevel--;
        }

        // ---------- 引き継ぐもの ----------
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("引き継ぐもの", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Position / Rotation は常に引き継ぎます", EditorStyles.miniLabel);

        keepScale = EditorGUILayout.Toggle("Scale", keepScale);
        keepName = EditorGUILayout.Toggle("名前", keepName);
        keepChildren = EditorGUILayout.Toggle(
            new GUIContent("子オブジェクト", "元オブジェクトの子を新しい方へ移動する"),
            keepChildren);

        EditorGUILayout.Space();

        // 名前が空のまま検索すると全オブジェクトが対象になるため、ボタン自体を押せなくする
        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(targetName)))
        {
            if (GUILayout.Button("検索", GUILayout.Height(24))) Search();
        }

        // ---------- 検索結果の一覧と実行ボタン（検索してからでないと表示されない） ----------
        if (found.Count > 0)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"対象: {found.Count} 件", EditorStyles.boldLabel);

            // 一覧の名前をクリックすると、シーン上で該当オブジェクトを選択・ハイライトする
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(120));
            foreach (var go in found)
            {
                if (go == null) continue; // 検索後に手動で削除された場合に備える
                if (GUILayout.Button(go.name, EditorStyles.miniButton))
                {
                    Selection.activeGameObject = go;
                    EditorGUIUtility.PingObject(go);
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();

            // Prefab が未指定なら実行できないようにする
            using (new EditorGUI.DisabledScope(prefab == null))
            {
                // 取り消しが重い操作なので、ボタンを赤くして注意を促す
                GUI.backgroundColor = new Color(1f, 0.6f, 0.6f);
                if (GUILayout.Button($"{found.Count} 件を置き換える", GUILayout.Height(28)))
                {
                    // 実行前に件数と置き換え先を最終確認させる
                    if (EditorUtility.DisplayDialog(
                        "確認",
                        $"{found.Count} 件を「{prefab.name}」に置き換えます。\n" +
                        "この操作は Ctrl+Z で元に戻せますが、\n" +
                        "元オブジェクト固有のコンポーネント設定は失われます。\n\n実行しますか?",
                        "実行する", "やめる"))
                    {
                        Replace();
                    }
                }
                GUI.backgroundColor = Color.white;
            }

            if (prefab == null)
                EditorGUILayout.HelpBox("Prefabを指定してください。", MessageType.Warning);
        }
    }

    /// <summary>名前が検索条件に一致するか（一致方法は MatchMode で切り替え）</summary>
    bool IsMatch(string name)
    {
        switch (matchMode)
        {
            case MatchMode.StartsWith: return name.StartsWith(targetName);
            case MatchMode.Contains: return name.Contains(targetName);
            case MatchMode.ExactMatch: return name == targetName;
        }
        return false;
    }

    /// <summary>
    /// シーン内から置換対象を集める。
    /// ここではシーンを一切変更せず、結果を一覧に出すだけにして、実行前に確認できるようにしている。
    /// </summary>
    void Search()
    {
        found.Clear();

        foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            // 1. 名前が一致しないものは除外
            if (!IsMatch(go.name)) continue;

            // 2. 既に Prefab のもの（置換済みのものなど）は除外
            if (skipPrefabInstances &&
                PrefabUtility.GetPrefabInstanceStatus(go) != PrefabInstanceStatus.NotAPrefab)
                continue;

            // 3. 指定コンポーネントを持たないもの（木の子の LOD0 など）は除外
            if (requireComponent)
            {
                // 入力された名前から UnityEngine の型を取得する（例: "LODGroup" → UnityEngine.LODGroup）
                var type = System.Type.GetType($"UnityEngine.{requiredComponentName}, UnityEngine");
                if (type == null || go.GetComponent(type) == null) continue;
            }

            found.Add(go);
        }

        Debug.Log($"[ObjectReplacer] {found.Count} 件見つかりました");
    }

    /// <summary>
    /// 検索結果を Prefab に一括で置き換える。
    /// 全件の操作を 1 つの Undo グループにまとめ、Ctrl+Z 1 回で全件を元に戻せるようにしている。
    /// </summary>
    void Replace()
    {
        // ここから先の操作を 1 つの Undo グループにまとめる
        Undo.SetCurrentGroupName("Object Replacer");
        int group = Undo.GetCurrentGroup();

        int count = 0;
        foreach (var old in found)
        {
            if (old == null) continue;

            // 元と同じ親の下に Prefab を生成する（Instantiate ではなく Prefab との繋がりを保つ）
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, old.transform.parent);

            // 位置・回転は常に引き継ぐ。Scale と名前は設定に応じて引き継ぐ
            inst.transform.SetPositionAndRotation(old.transform.position, old.transform.rotation);
            if (keepScale) inst.transform.localScale = old.transform.localScale;
            if (keepName) inst.name = old.name;

            // Hierarchy 上の並び順も元と同じ位置にする
            inst.transform.SetSiblingIndex(old.transform.GetSiblingIndex());

            if (keepChildren)
            {
                // 逆順に回さないと、移動によって添字がずれる
                for (int i = old.transform.childCount - 1; i >= 0; i--)
                {
                    var child = old.transform.GetChild(i);
                    Undo.SetTransformParent(child, inst.transform, "reparent");
                }
            }

            // 生成と削除を Undo に記録してから元オブジェクトを消す
            Undo.RegisterCreatedObjectUndo(inst, "create");
            Undo.DestroyObjectImmediate(old);
            count++;
        }

        // ここまでの操作を 1 つにまとめ、Ctrl+Z 1 回で全件を戻せるようにする
        Undo.CollapseUndoOperations(group);

        // 置換後の一覧は削除済みオブジェクトを指すので空にする
        found.Clear();
        Debug.Log($"[ObjectReplacer] {count} 件を置き換えました");
    }
}
