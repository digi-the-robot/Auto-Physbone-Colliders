using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Digi.AutoPhysBoneColliders
{
    public class AutoPhysBoneCollidersWindow : EditorWindow
    {
        public const string Version = "1.0.0";
        const string DefaultContainerName = "PhysBone Colliders (Auto)";

        [Serializable]
        class PartOverride
        {
            public Transform transform;
            public PartMode mode;
            public int split = 1;
        }

        [Serializable]
        class RendererOverride
        {
            public Renderer renderer;
            public bool include;
        }

        [SerializeField] GameObject root;
        [SerializeField] FitSettings settings = new FitSettings();
        [SerializeField] HumanoidPreset preset = HumanoidPreset.Standard;
        [SerializeField] List<PartOverride> partOverrides = new List<PartOverride>();
        [SerializeField] List<RendererOverride> rendererOverrides = new List<RendererOverride>();
        [SerializeField] List<Component> assignTo = new List<Component>();
        [SerializeField] string containerName = DefaultContainerName;
        [SerializeField] bool replaceExisting = true;
        [SerializeField] bool showPreview = true;
        [SerializeField] bool hideInactiveParts;
        [SerializeField] bool foldMeshes = true;
        [SerializeField] bool foldFit = true;
        [SerializeField] bool foldParts = true;
        [SerializeField] bool foldOutput = true;
        [SerializeField] bool foldAdvanced;

        ModelAnalysis analysis;
        Part selectedPart;
        Vector2 scroll;
        string search = "";
        bool needsRefit;

        static readonly Color CapsuleColor = new Color(0.25f, 1f, 0.45f, 1f);
        static readonly Color SphereColor = new Color(0.3f, 0.8f, 1f, 1f);
        static readonly Color PlaneColor = new Color(1f, 0.8f, 0.2f, 1f);
        static readonly Color SelectedColor = new Color(1f, 0.35f, 0.9f, 1f);

        [MenuItem("Tools/Digi The Robot/Auto PhysBone Colliders")]
        static void OpenFromMenu()
        {
            Open();
        }

        public static AutoPhysBoneCollidersWindow Open()
        {
            var w = GetWindow<AutoPhysBoneCollidersWindow>();
            w.titleContent = new GUIContent("Auto PB Colliders");
            w.minSize = new Vector2(380, 420);
            if (w.root == null) w.TryPickRootFromSelection();
            return w;
        }

        [MenuItem("GameObject/Digi The Robot/Auto PhysBone Colliders", false, 30)]
        static void OpenFromHierarchy()
        {
            var w = Open();
            w.TryPickRootFromSelection();
        }

        void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            Undo.undoRedoPerformed += OnUndoRedo;
            // Mesh access during a domain reload is unreliable; analyze once the editor settles.
            EditorApplication.delayCall += () => { if (this != null && root != null && analysis == null) Analyze(); };
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;
            SceneView.RepaintAll();
        }

        void OnUndoRedo()
        {
            if (root == null) { analysis = null; Repaint(); return; }
            needsRefit = true;
            Repaint();
        }

        void OnHierarchyChange()
        {
            if (analysis != null && root == null) { analysis = null; Repaint(); }
        }

        void TryPickRootFromSelection()
        {
            var go = Selection.activeGameObject;
            if (go == null || EditorUtility.IsPersistent(go)) return;

            // Prefer the avatar root (descriptor), then the top-most Animator, then the selection itself.
            GameObject pick = go;
            foreach (var c in go.GetComponentsInParent<Component>(true))
            {
                if (c != null && c.GetType().Name == "VRCAvatarDescriptor") { pick = c.gameObject; break; }
            }
            if (pick == go)
            {
                var anims = go.GetComponentsInParent<Animator>(true);
                if (anims.Length > 0) pick = anims[anims.Length - 1].gameObject;
            }
            SetRoot(pick);
        }

        void SetRoot(GameObject go)
        {
            if (go == root && analysis != null) return;
            root = go;
            partOverrides.Clear();
            rendererOverrides.Clear();
            assignTo.Clear();
            selectedPart = null;
            analysis = null;
            if (root != null) Analyze();
            Repaint();
        }

        // ================================================================ analysis

        void Analyze()
        {
            if (root == null || !PhysBoneApi.Available) { analysis = null; return; }

            analysis = ModelAnalysis.Build(root, r =>
            {
                foreach (var o in rendererOverrides) if (o.renderer == r) return o.include;
                return null;
            }, settings);
            analysis.ApplyDefaults(preset, settings);

            foreach (var o in partOverrides)
            {
                Part p;
                if (o.transform != null && analysis.partByTransform.TryGetValue(o.transform, out p))
                {
                    p.mode = o.mode;
                    p.split = Mathf.Max(1, o.split);
                }
            }
            selectedPart = null;
            Refit();
        }

        void Refit()
        {
            needsRefit = false;
            if (analysis == null) return;
            analysis.Refit(settings);
            SceneView.RepaintAll();
        }

        void RememberOverride(Part p)
        {
            foreach (var o in partOverrides)
            {
                if (o.transform == p.transform) { o.mode = p.mode; o.split = p.split; return; }
            }
            partOverrides.Add(new PartOverride { transform = p.transform, mode = p.mode, split = p.split });
        }

        // ================================================================ GUI

        void OnGUI()
        {
            if (needsRefit) Refit();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Auto PhysBone Colliders", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Fits VRC PhysBone colliders to the shape of your meshes.  v" + Version, EditorStyles.miniLabel);

            if (!PhysBoneApi.Available)
            {
                EditorGUILayout.HelpBox("VRChat PhysBones weren't found in this project.\nImport the VRChat Avatars SDK (3.0 or newer) to use this tool.", MessageType.Error);
                return;
            }

            EditorGUI.BeginChangeCheck();
            var newRoot = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Avatar / Model", "Root object. Colliders are generated for meshes under it."), root, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck())
            {
                if (newRoot != null && EditorUtility.IsPersistent(newRoot))
                {
                    ShowNotification(new GUIContent("Drag in an object from the scene, not a prefab asset."));
                    newRoot = null;
                }
                SetRoot(newRoot);
                GUIUtility.ExitGUI();
            }

            if (root == null)
            {
                EditorGUILayout.HelpBox("Pick an avatar or model in the scene (or select one and click the button).", MessageType.Info);
                if (GUILayout.Button("Use Selection"))
                {
                    TryPickRootFromSelection();
                    GUIUtility.ExitGUI();
                }
                return;
            }

            if (analysis == null) Analyze();
            if (analysis == null) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(analysis.isHumanoid ? "Humanoid rig" : "Generic / non-humanoid", EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Re-scan", "Re-read meshes (after changing blendshapes, pose or outfits)."), EditorStyles.miniButton, GUILayout.Width(70)))
                {
                    Analyze();
                    GUIUtility.ExitGUI();
                }
            }

            foreach (var w in analysis.warnings) EditorGUILayout.HelpBox(w, MessageType.Warning);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawMeshesSection();
            DrawFitSection();
            DrawPartsSection();
            DrawOutputSection();
            EditorGUILayout.EndScrollView();

            DrawGenerateBar();
        }

        void DrawMeshesSection()
        {
            foldMeshes = Header(foldMeshes, "Meshes (" + CountIncluded() + "/" + analysis.renderers.Count + ")");
            if (!foldMeshes) return;

            EditorGUI.indentLevel++;
            bool changed = false;
            foreach (var r in analysis.renderers)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var label = new GUIContent(r.renderer.name + (r.skinned ? "" : "  (static)"),
                        r.vertexCount + " vertices" + (r.renderer.gameObject.activeInHierarchy ? "" : ", inactive"));
                    bool inc = EditorGUILayout.ToggleLeft(label, r.include);
                    if (inc != r.include)
                    {
                        r.include = inc;
                        SetRendererOverride(r.renderer, inc);
                        changed = true;
                    }
                    if (!string.IsNullOrEmpty(r.error)) GUILayout.Label(EditorGUIUtility.IconContent("console.warnicon.sml"), GUILayout.Width(18));
                    if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(48))) Selection.activeObject = r.renderer.gameObject;
                }
            }
            EditorGUI.indentLevel--;
            EditorGUILayout.LabelField("Hair and other PhysBone-driven parts are skipped automatically. Untick meshes that shouldn't push hair around (hidden outfits, effects).", EditorStyles.wordWrappedMiniLabel);
            if (changed)
            {
                Analyze();
                GUIUtility.ExitGUI();
            }
        }

        int CountIncluded()
        {
            int n = 0;
            foreach (var r in analysis.renderers) if (r.include) n++;
            return n;
        }

        void SetRendererOverride(Renderer r, bool include)
        {
            foreach (var o in rendererOverrides) if (o.renderer == r) { o.include = include; return; }
            rendererOverrides.Add(new RendererOverride { renderer = r, include = include });
        }

        void DrawFitSection()
        {
            foldFit = Header(foldFit, "Fit");
            if (!foldFit) return;

            EditorGUI.BeginChangeCheck();
            settings.tightness = EditorGUILayout.Slider(new GUIContent("Tightness", "Lower = colliders sit inside the surface, higher = they wrap around it."), settings.tightness, 0.2f, 1f);
            settings.radiusScale = EditorGUILayout.Slider(new GUIContent("Radius Scale", "Multiplier on every radius."), settings.radiusScale, 0.5f, 1.5f);
            settings.capsuleThreshold = EditorGUILayout.Slider(new GUIContent("Capsule Threshold", "Auto uses a capsule when length / diameter is at least this; otherwise a sphere."), settings.capsuleThreshold, 1f, 3f);
            if (analysis.isHumanoid)
                settings.symmetrize = EditorGUILayout.Toggle(new GUIContent("Mirror Left/Right", "Average left/right pairs so both sides match."), settings.symmetrize);

            foldAdvanced = EditorGUILayout.Foldout(foldAdvanced, "Advanced", true);
            bool rescan = false;
            if (foldAdvanced)
            {
                EditorGUI.indentLevel++;
                settings.outlierTrim = EditorGUILayout.Slider(new GUIContent("Outlier Trim", "Fraction of vertices ignored at each end of every axis."), settings.outlierTrim, 0f, 0.15f);
                EditorGUI.BeginChangeCheck();
                settings.minBoneWeight = EditorGUILayout.Slider(new GUIContent("Min Bone Weight", "A vertex counts toward its strongest bone only if that weight is at least this."), settings.minBoneWeight, 0f, 0.9f);
                if (EditorGUI.EndChangeCheck()) rescan = true;
                if (!analysis.isHumanoid)
                {
                    EditorGUI.BeginChangeCheck();
                    settings.minVertices = EditorGUILayout.IntField(new GUIContent("Min Vertices", "Smaller parts merge into their parent (applies when resetting defaults)."), settings.minVertices);
                    settings.minSizeCm = EditorGUILayout.FloatField(new GUIContent("Min Size (cm)", "Smaller parts merge into their parent (applies when resetting defaults)."), settings.minSizeCm);
                    EditorGUI.EndChangeCheck();
                }
                if (GUILayout.Button("Reset Fit Settings")) { settings = new FitSettings(); rescan = true; }
                EditorGUI.indentLevel--;
            }
            if (EditorGUI.EndChangeCheck())
            {
                if (rescan)
                {
                    Analyze();
                    GUIUtility.ExitGUI();
                }
                Refit();
            }
        }

        void DrawPartsSection()
        {
            foldParts = Header(foldParts, "Parts");
            if (!foldParts) return;

            if (analysis.isHumanoid)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel(new GUIContent("Preset", "Light ≈ 7 colliders (Quest friendly), Standard ≈ 13, Detailed adds hands, feet and upper chest."));
                    foreach (HumanoidPreset p in Enum.GetValues(typeof(HumanoidPreset)))
                    {
                        var style = p == preset ? EditorStyles.miniButtonMid : EditorStyles.miniButton;
                        GUI.color = p == preset ? new Color(0.6f, 1f, 0.7f) : Color.white;
                        bool clicked = GUILayout.Button(p.ToString(), style);
                        GUI.color = Color.white;
                        if (clicked)
                        {
                            ApplyPreset(p);
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }
            else if (GUILayout.Button("Reset Parts to Defaults"))
            {
                ApplyPreset(preset);
                GUIUtility.ExitGUI();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                search = EditorGUILayout.TextField(search, GUILayout.MinWidth(80));
                hideInactiveParts = GUILayout.Toggle(hideInactiveParts, new GUIContent("Colliders only", "Hide merged and skipped parts."), EditorStyles.miniButton, GUILayout.Width(95));
            }

            bool changed = false;
            foreach (var part in analysis.parts)
            {
                if (hideInactiveParts && !part.IsCollider) continue;
                if (!string.IsNullOrEmpty(search) &&
                    part.label.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                    part.transform.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;

                changed |= DrawPartRow(part);
            }
            if (analysis.parts.Count == 0) EditorGUILayout.HelpBox("No vertices found. Check that at least one mesh is included.", MessageType.Info);
            if (changed)
            {
                // A mode change can add or remove the split field, so end this GUI pass cleanly.
                Refit();
                GUIUtility.ExitGUI();
            }
        }

        bool DrawPartRow(Part part)
        {
            bool changed = false;
            var rowRect = EditorGUILayout.BeginHorizontal();
            if (part == selectedPart) EditorGUI.DrawRect(rowRect, new Color(1f, 0.35f, 0.9f, 0.15f));

            GUILayout.Space(4 + Mathf.Min(part.depth, 12) * 10);
            var labelStyle = part.IsCollider ? EditorStyles.boldLabel : EditorStyles.label;
            var tooltip = part.transform.name + "\n" + part.ownPointCount + " vertices" + (part.physBoneDriven ? "\nDriven by a PhysBone" : "");
            string prefix = part.physBoneDriven ? "~ " : "";
            if (GUILayout.Button(new GUIContent(prefix + part.label, tooltip), labelStyle, GUILayout.MinWidth(60)))
            {
                selectedPart = selectedPart == part ? null : part;
                EditorGUIUtility.PingObject(part.transform);
                SceneView.RepaintAll();
            }
            GUILayout.FlexibleSpace();

            var mode = (PartMode)EditorGUILayout.EnumPopup(part.mode, GUILayout.Width(72));
            if (mode == PartMode.Plane && !PhysBoneApi.SupportsShape(ColliderShape.Plane)) mode = part.mode;
            if (mode != part.mode) { part.mode = mode; RememberOverride(part); changed = true; }

            if (part.IsCollider)
            {
                int split = EditorGUILayout.IntField(new GUIContent("", "Split into this many shapes."), part.split, GUILayout.Width(26));
                split = Mathf.Clamp(split, 1, 16);
                if (split != part.split) { part.split = split; RememberOverride(part); changed = true; }
            }
            else GUILayout.Space(30);

            GUILayout.Label(Summary(part), EditorStyles.miniLabel, GUILayout.Width(120));
            EditorGUILayout.EndHorizontal();
            return changed;
        }

        static string Summary(Part part)
        {
            if (part.mode == PartMode.Skip) return "skipped";
            if (part.mode == PartMode.Merge)
                return part.target != null ? "→ " + part.target.label : "→ nothing (skipped)";
            if (part.results.Count == 0) return "too few vertices";
            if (part.results.Count > 1) return part.results.Count + " shapes";
            var r = part.results[0];
            switch (r.shape)
            {
                case ColliderShape.Capsule: return string.Format("capsule r{0:0.#} h{1:0.#}cm", r.radius * 100f, r.height * 100f);
                case ColliderShape.Sphere: return string.Format("sphere r{0:0.#}cm", r.radius * 100f);
                default: return "plane";
            }
        }

        void ApplyPreset(HumanoidPreset p)
        {
            preset = p;
            partOverrides.Clear();
            analysis.ApplyDefaults(preset, settings);
            Refit();
        }

        void DrawOutputSection()
        {
            foldOutput = Header(foldOutput, "Output");
            if (!foldOutput) return;

            containerName = EditorGUILayout.TextField(new GUIContent("Container Name", "Colliders are created as children of this object under the avatar root. Your armature is not modified."), containerName);
            replaceExisting = EditorGUILayout.Toggle(new GUIContent("Replace Previous", "Delete the previously generated container first. PhysBones that used those colliders get re-linked by name."), replaceExisting);

            if (analysis.physBones.Count > 0)
            {
                EditorGUILayout.LabelField("Add new colliders to these PhysBones:", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                foreach (var pb in analysis.physBones)
                {
                    if (pb == null) continue;
                    bool on = assignTo.Contains(pb);
                    var pbRoot = PhysBoneApi.GetPhysBoneRoot(pb);
                    var label = pb.gameObject.name + (pbRoot != pb.transform ? "  (" + pbRoot.name + ")" : "");
                    bool now = EditorGUILayout.ToggleLeft(label, on);
                    if (now && !on) assignTo.Add(pb);
                    if (!now && on) assignTo.Remove(pb);
                }
                EditorGUI.indentLevel--;
                EditorGUILayout.LabelField("Every PhysBone × collider pair costs a collision check, so only tick chains that need them (long hair, skirts, tails).", EditorStyles.wordWrappedMiniLabel);
            }
        }

        void DrawGenerateBar()
        {
            EditorGUILayout.Space();
            int newCount = analysis.ColliderCount;
            int existing = CountExistingColliders();
            int total = existing + newCount;

            showPreview = EditorGUILayout.ToggleLeft("Preview in Scene view", showPreview);
            if (GUI.changed) SceneView.RepaintAll();

            var msg = string.Format("{0} new collider{1}{2}. Avatar total: {3}  →  PC {4}, Quest {5}.",
                newCount, newCount == 1 ? "" : "s",
                existing > 0 ? " + " + existing + " existing" : "",
                total, PcRank(total), QuestRank(total));
            var type = total > 16 ? MessageType.Warning : MessageType.Info;
            EditorGUILayout.HelpBox(msg + (total > 16 ? "\nOver 16 colliders is Very Poor on Quest/Android, which disables every PhysBone on the avatar there." : ""), type);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = newCount > 0;
                bool generate = GUILayout.Button("Generate " + newCount + " Collider" + (newCount == 1 ? "" : "s"), GUILayout.Height(30));
                GUI.enabled = FindContainer() != null;
                bool remove = GUILayout.Button("Remove Generated", GUILayout.Height(30), GUILayout.Width(130));
                GUI.enabled = true;
                if (generate || remove)
                {
                    if (generate) Generate();
                    else RemoveGenerated();
                    GUIUtility.ExitGUI();
                }
            }
        }

        static string PcRank(int n)
        {
            if (n <= 4) return "Excellent";
            if (n <= 8) return "Good";
            if (n <= 16) return "Medium";
            if (n <= 32) return "Poor";
            return "Very Poor";
        }

        static string QuestRank(int n)
        {
            if (n == 0) return "Excellent";
            if (n <= 4) return "Good";
            if (n <= 8) return "Medium";
            if (n <= 16) return "Poor";
            return "Very Poor";
        }

        int CountExistingColliders()
        {
            if (root == null) return 0;
            var container = replaceExisting ? FindContainer() : null;
            int n = 0;
            foreach (var c in root.GetComponentsInChildren(PhysBoneApi.ColliderType, true))
            {
                if (container != null && c.transform.IsChildOf(container)) continue;
                n++;
            }
            return n;
        }

        static bool Header(bool open, string title)
        {
            EditorGUILayout.Space();
            var r = EditorGUILayout.GetControlRect(false, 20);
            EditorGUI.DrawRect(r, EditorGUIUtility.isProSkin ? new Color(1, 1, 1, 0.06f) : new Color(0, 0, 0, 0.08f));
            return EditorGUI.Foldout(r, open, title, true, EditorStyles.foldout);
        }

        // ================================================================ generation

        Transform FindContainer()
        {
            if (root == null) return null;
            var t = root.transform.Find(containerName);
            if (t != null) return t;
            return root.transform.Find(DefaultContainerName);
        }

        void RemoveGenerated()
        {
            var container = FindContainer();
            if (container == null) return;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Remove Generated PhysBone Colliders");
            int group = Undo.GetCurrentGroup();
            foreach (var pb in analysis.physBones)
            {
                if (pb != null) PhysBoneApi.RemoveColliders(pb, c => c.transform.IsChildOf(container), false);
            }
            Undo.DestroyObjectImmediate(container.gameObject);
            Undo.CollapseUndoOperations(group);
        }

        void Generate()
        {
            if (analysis == null || root == null) return;
            Refit();

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Generate PhysBone Colliders");
            int group = Undo.GetCurrentGroup();

            // Remember which PhysBones used the old colliders so they can be re-linked by name.
            var relink = new List<KeyValuePair<Component, string>>();
            var old = replaceExisting ? FindContainer() : null;
            if (old != null)
            {
                foreach (var pb in analysis.physBones)
                {
                    if (pb == null) continue;
                    foreach (var c in PhysBoneApi.GetColliders(pb))
                    {
                        if (c != null && c.transform.IsChildOf(old)) relink.Add(new KeyValuePair<Component, string>(pb, c.gameObject.name));
                    }
                    PhysBoneApi.RemoveColliders(pb, c => c.transform.IsChildOf(old), false);
                }
                Undo.DestroyObjectImmediate(old.gameObject);
            }

            var name = string.IsNullOrEmpty(containerName) ? DefaultContainerName : containerName;
            var container = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(container, "Create collider container");
            container.transform.SetParent(root.transform, false);

            var created = new List<Component>();
            var byName = new Dictionary<string, Component>();
            var usedNames = new HashSet<string>();
            foreach (var part in analysis.parts)
            {
                if (!part.IsCollider) continue;
                for (int i = 0; i < part.results.Count; i++)
                {
                    var goName = "PBC " + part.label + (part.results.Count > 1 ? " " + (i + 1) : "");
                    int dup = 2;
                    var baseName = goName;
                    while (!usedNames.Add(goName)) goName = baseName + " (" + dup++ + ")";

                    var go = new GameObject(goName);
                    Undo.RegisterCreatedObjectUndo(go, "Create collider");
                    go.transform.SetParent(container.transform, false);
                    var collider = PhysBoneApi.CreateCollider(go, part.transform, part.results[i]);
                    created.Add(collider);
                    byName[goName] = collider;
                }
            }

            foreach (var kv in relink)
            {
                Component c;
                if (kv.Key != null && byName.TryGetValue(kv.Value, out c)) PhysBoneApi.AddColliders(kv.Key, new[] { c });
            }
            foreach (var pb in assignTo)
            {
                if (pb != null) PhysBoneApi.AddColliders(pb, created);
            }

            Undo.CollapseUndoOperations(group);
            EditorGUIUtility.PingObject(container);
            ShowNotification(new GUIContent("Created " + created.Count + " collider" + (created.Count == 1 ? "" : "s")));
            Debug.Log("[Auto PhysBone Colliders] Created " + created.Count + " colliders under '" + root.name + "/" + name + "'.", container);
        }

        // ================================================================ scene preview

        void OnSceneGUI(SceneView view)
        {
            if (!showPreview || analysis == null || root == null) return;
            if (Event.current.type != EventType.Repaint) return;

            var oldZ = Handles.zTest;
            foreach (var part in analysis.parts)
            {
                if (!part.IsCollider) continue;
                bool selected = part == selectedPart ||
                                (selectedPart != null && selectedPart.target == part);
                foreach (var r in part.results)
                {
                    var color = selected ? SelectedColor : r.shape == ColliderShape.Capsule ? CapsuleColor : r.shape == ColliderShape.Sphere ? SphereColor : PlaneColor;

                    // Colliders sit just under the skin, so the hidden pass has to stay readable.
                    Handles.zTest = CompareFunction.Greater;
                    Handles.color = new Color(color.r, color.g, color.b, selected ? 0.9f : 0.55f);
                    DrawShape(r);

                    Handles.zTest = CompareFunction.LessEqual;
                    Handles.color = color;
                    DrawShape(r);
                }
            }
            Handles.zTest = oldZ;
        }

        static void DrawShape(FitResult r)
        {
            switch (r.shape)
            {
                case ColliderShape.Capsule: DrawCapsule(r.center, r.axis, r.radius, r.height); break;
                case ColliderShape.Sphere: DrawSphere(r.center, r.radius); break;
                case ColliderShape.Plane: DrawPlane(r.center, r.axis); break;
            }
        }

        static void DrawSphere(Vector3 c, float r)
        {
            Handles.DrawWireDisc(c, Vector3.up, r);
            Handles.DrawWireDisc(c, Vector3.right, r);
            Handles.DrawWireDisc(c, Vector3.forward, r);
            var cam = Camera.current;
            if (cam != null) Handles.DrawWireDisc(c, (cam.transform.position - c).normalized, r);
        }

        static readonly Vector3[] arc = new Vector3[17];

        static void DrawCapsule(Vector3 c, Vector3 axis, float r, float h)
        {
            axis = axis.normalized;
            float half = Mathf.Max(0f, h * 0.5f - r);
            var top = c + axis * half;
            var bottom = c - axis * half;
            var p1 = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var p2 = Vector3.Cross(axis, p1);

            Handles.DrawWireDisc(top, axis, r);
            Handles.DrawWireDisc(bottom, axis, r);
            Handles.DrawLine(top + p1 * r, bottom + p1 * r);
            Handles.DrawLine(top - p1 * r, bottom - p1 * r);
            Handles.DrawLine(top + p2 * r, bottom + p2 * r);
            Handles.DrawLine(top - p2 * r, bottom - p2 * r);
            DrawCap(top, axis, p1, r);
            DrawCap(top, axis, p2, r);
            DrawCap(bottom, -axis, p1, r);
            DrawCap(bottom, -axis, p2, r);
        }

        static void DrawCap(Vector3 center, Vector3 outward, Vector3 side, float r)
        {
            for (int i = 0; i < arc.Length; i++)
            {
                float a = Mathf.PI * i / (arc.Length - 1);
                arc[i] = center + (side * Mathf.Cos(a) + outward * Mathf.Sin(a)) * r;
            }
            Handles.DrawPolyLine(arc);
        }

        static void DrawPlane(Vector3 c, Vector3 normal)
        {
            var p1 = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized * 0.5f;
            var p2 = Vector3.Cross(normal, p1);
            Handles.DrawLine(c + p1 + p2, c + p1 - p2);
            Handles.DrawLine(c + p1 - p2, c - p1 - p2);
            Handles.DrawLine(c - p1 - p2, c - p1 + p2);
            Handles.DrawLine(c - p1 + p2, c + p1 + p2);
            Handles.DrawLine(c, c + normal * 0.25f);
        }
    }
}
