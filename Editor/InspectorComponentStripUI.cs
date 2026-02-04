using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace InspectorComponentStrip
{
    [InitializeOnLoad]
    public static class InspectorComponentStripUI
    {
        private static readonly List<ComponentStrip> Strips = new();

        static InspectorComponentStripUI()
        {
            EditorApplication.delayCall += HookAllInspectors;
            EditorApplication.update += HookNewInspectorsTick;

            Selection.selectionChanged += () =>
            {
                foreach (var strip in Strips.ToArray())
                {
                    if (strip == null) continue;
                    if (IsInspectorLocked(strip.Owner)) continue;
                    strip.RestoreAllTouched();
                    strip.Rebuild();
                }
            };

            AssemblyReloadEvents.beforeAssemblyReload += RestoreAllStripsTouched;
            EditorApplication.quitting += RestoreAllStripsTouched;
        }

        private static void RestoreAllStripsTouched()
        {
            foreach (var strip in Strips.ToArray())
                if (strip != null)
                    strip.RestoreAllTouched();
        }

        private static void HookAllInspectors()
        {
            foreach (var win in FindAllInspectors())
                EnsureStrip(win);
        }

        private static void HookNewInspectorsTick()
        {
            foreach (var win in FindAllInspectors())
                EnsureStrip(win);
        }

        private static IEnumerable<EditorWindow> FindAllInspectors()
        {
            var inspectorType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.InspectorWindow");
            if (inspectorType == null) yield break;

            var objs = Resources.FindObjectsOfTypeAll(inspectorType);
            foreach (var o in objs)
                if (o is EditorWindow ew)
                    yield return ew;
        }

        private static void EnsureStrip(EditorWindow win)
        {
            if (!win) return;
            var root = win.rootVisualElement;

            if (root.Q<ComponentStrip>("component-strip") != null) return;

            var strip = new ComponentStrip(win) { name = "component-strip" };
            strip.style.marginLeft = 6;
            strip.style.marginRight = 6;
            strip.style.marginTop = 4;
            strip.style.marginBottom = 4;
            strip.style.flexDirection = FlexDirection.Row;
            strip.style.flexWrap = Wrap.Wrap;

            root.Insert(0, strip);
            Strips.Add(strip);

            strip.Rebuild();
        }

        private static void ForceInspectorRebuild(EditorWindow owner, bool respectLock)
        {
            if (!owner) return;
            if (respectLock && IsInspectorLocked(owner)) { owner.Repaint(); return; }

            var tracker = GetTrackerForWindow(owner);
            if (tracker == null) { InternalEditorUtility.RepaintAllViews(); return; }

            tracker.ForceRebuild();
            owner.Repaint();

            EditorApplication.delayCall += () =>
            {
                if (!owner) return;
                var tr2 = GetTrackerForWindow(owner);
                tr2?.ForceRebuild();
                owner.Repaint();
            };
        }

        private static ActiveEditorTracker GetTrackerForWindow(EditorWindow owner)
        {
            if (!owner) return null;
            var inspectorType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.InspectorWindow");
            if (inspectorType == null || !inspectorType.IsInstanceOfType(owner)) return null;

            var prop = inspectorType.GetProperty("tracker",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return (ActiveEditorTracker)prop?.GetValue(owner);
        }

        private static bool IsInspectorLocked(EditorWindow owner)
        {
            var tracker = GetTrackerForWindow(owner);
            return tracker != null && tracker.isLocked;
        }

        private static GameObject GetInspectedGameObjectFor(EditorWindow owner)
        {
            var tracker = GetTrackerForWindow(owner);
            if (tracker == null)
                return Selection.activeGameObject;

            var editors = tracker.activeEditors;
            if (editors == null || editors.Length == 0)
                return Selection.activeGameObject;

            foreach (var ed in editors)
            {
                var t = ed?.target;
                if (t is Component comp && comp) return comp.gameObject;
                if (t is GameObject go && go) return go;
            }

            var first = editors[0]?.target;
            if (first is Component c && c) return c.gameObject;
            if (first is GameObject g && g) return g;

            return Selection.activeGameObject;
        }

        private sealed class ComponentStrip : VisualElement
        {
            public EditorWindow Owner { get; }

            private readonly Dictionary<Type, GUIContent> _contentCache = new();

            private readonly Dictionary<int, HashSet<int>> _selectedByGO = new();

            private readonly Dictionary<int, Dictionary<int, HideFlags>> _originalFlagsByGO = new();

            private double _lastClickTime;
            private int _lastClickCompId = -1;
            private const double DOUBLE_CLICK_THRESHOLD = 0.30; // seconds

            public ComponentStrip(EditorWindow owner) => Owner = owner;

            public void RestoreAllTouched()
            {
                foreach (var kv in _originalFlagsByGO)
                {
                    int goID = kv.Key;
                    var go = EditorUtility.InstanceIDToObject(goID) as GameObject;
                    if (!go) continue;

                    var map = kv.Value;
                    var comps = go.GetComponents<Component>();
                    foreach (var c in comps)
                    {
                        if (!c) continue;
                        int cid = c.GetInstanceID();
                        if (!map.TryGetValue(cid, out var orig)) continue;
                        if (c.hideFlags != orig)
                            c.hideFlags = orig;
                    }
                }

                _selectedByGO.Clear();
                _originalFlagsByGO.Clear();
                ForceInspectorRebuild(Owner, true);
            }

            public void Rebuild()
            {
                Clear();

                var go = GetInspectedGameObjectFor(Owner);
                if (!go) return;

                var comps = go.GetComponents<Component>();
                if (comps == null || comps.Length == 0) return;

                int goID = go.GetInstanceID();

                if (!_originalFlagsByGO.ContainsKey(goID))
                {
                    var map = new Dictionary<int, HideFlags>(64);
                    foreach (var c in comps)
                        if (c)
                            map[c.GetInstanceID()] = c.hideFlags;
                    _originalFlagsByGO[goID] = map;
                }

                _selectedByGO.TryGetValue(goID, out var selected);

                foreach (var c in comps)
                {
                    if (!c) continue;

                    var t = c.GetType();
                    int compId = c.GetInstanceID();

                    var orig = _originalFlagsByGO[goID].TryGetValue(compId, out var oflags) ? oflags : c.hideFlags;
                    bool originallyHidden = (orig & HideFlags.HideInInspector) != 0;

                    bool isTransform = c is Transform;
                    if (originallyHidden && !isTransform) continue;

                    if (!_contentCache.TryGetValue(t, out var gc))
                    {
                        var icon = EditorGUIUtility.ObjectContent(null, t)?.image;
                        gc = new GUIContent(ObjectNames.NicifyVariableName(t.Name), icon, t.FullName);
                        _contentCache[t] = gc;
                    }

                    bool isOn = selected != null && selected.Contains(compId);
                    var chip = MakeChip(gc, isOn);

                    chip.RegisterCallback<ClickEvent>(evt =>
                    {
                        if (evt.button != 0) return; // LMB only
                        evt.StopImmediatePropagation();

                        var now = EditorApplication.timeSinceStartup;
                        bool isDouble = (compId == _lastClickCompId) && (now - _lastClickTime <= DOUBLE_CLICK_THRESHOLD);
                        _lastClickCompId = compId;
                        _lastClickTime = now;

                        if (isDouble)
                        {
                            // Reset this GO entirely
                            _selectedByGO.Remove(goID);
                            RestoreGO(goID);
                            Rebuild();
                            return;
                        }

                        if (selected == null)
                            selected = _selectedByGO[goID] = new HashSet<int>();

                        if (!selected.Add(compId))
                            selected.Remove(compId);

                        ApplyHideFlags(go, selected);

                        Rebuild();
                    });

                    Add(chip);
                }
            }

            private void RestoreGO(int goID)
            {
                if (!_originalFlagsByGO.TryGetValue(goID, out var map)) return;

                var go = EditorUtility.InstanceIDToObject(goID) as GameObject;
                if (!go) return;

                var comps = go.GetComponents<Component>();
                foreach (var c in comps)
                {
                    if (!c) continue;
                    int cid = c.GetInstanceID();
                    if (!map.TryGetValue(cid, out var orig)) continue;
                    if (c.hideFlags != orig)
                        c.hideFlags = orig;
                }

                ForceInspectorRebuild(Owner, false);
            }

            private void ApplyHideFlags(GameObject go, HashSet<int> selectedIds)
            {
                int goID = go.GetInstanceID();
                if (!_originalFlagsByGO.TryGetValue(goID, out var originals))
                {
                    originals = new Dictionary<int, HideFlags>(64);
                    foreach (var c in go.GetComponents<Component>())
                        if (c)
                            originals[c.GetInstanceID()] = c.hideFlags;
                    _originalFlagsByGO[goID] = originals;
                }

                var comps = go.GetComponents<Component>();
                bool showAll = selectedIds == null || selectedIds.Count == 0;

                foreach (var c in comps)
                {
                    if (!c) continue;

                    int cid = c.GetInstanceID();
                    if (!originals.TryGetValue(cid, out var orig))
                        orig = originals[cid] = c.hideFlags; // capture late-joined components

                    bool originallyHidden = (orig & HideFlags.HideInInspector) != 0;
                    bool isTransform = c is Transform;

                    HideFlags newFlags;

                    if (showAll)
                    {
                        newFlags = orig;
                    }
                    else
                    {
                        if (originallyHidden && !isTransform)
                        {
                            newFlags = orig;
                        }
                        else
                        {
                            bool shouldShow = isTransform || selectedIds.Contains(cid);

                            newFlags = shouldShow
                                ? (orig & ~HideFlags.HideInInspector)
                                : (orig | HideFlags.HideInInspector);
                        }
                    }

                    if (c.hideFlags != newFlags)
                        c.hideFlags = newFlags;
                }

                ForceInspectorRebuild(Owner, false);
            }
        }

        private static Button MakeChip(GUIContent gc, bool on)
        {
            var btn = new Button { text = string.Empty, tooltip = gc.tooltip };
            btn.style.height = 20;
            btn.style.paddingLeft = 8;
            btn.style.paddingRight = 8;
            btn.style.paddingTop = 2;
            btn.style.paddingBottom = 2;
            btn.style.borderTopLeftRadius = 6;
            btn.style.borderTopRightRadius = 6;
            btn.style.borderBottomLeftRadius = 6;
            btn.style.borderBottomRightRadius = 6;
            btn.style.borderLeftWidth = 1;
            btn.style.borderRightWidth = 1;
            btn.style.borderTopWidth = 1;
            btn.style.borderBottomWidth = 1;

            var border = new Color(0, 0, 0, 0.35f);
            btn.style.borderLeftColor = border;
            btn.style.borderRightColor = border;
            btn.style.borderTopColor = border;
            btn.style.borderBottomColor = border;
            btn.style.backgroundColor = on
                ? new Color(0.33f, 0.52f, 0.89f, 0.35f)
                : new Color(0.24f, 0.24f, 0.24f, 1f);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            if (gc.image is Texture tex)
            {
                var img = new Image { image = tex };
                img.style.width = 12;
                img.style.height = 12;
                img.style.marginRight = 6;
                row.Add(img);
            }

            var lbl = new Label(gc.text);
            lbl.style.fontSize = 11;
            row.Add(lbl);
            btn.Add(row);

            return btn;
        }
    }
}
