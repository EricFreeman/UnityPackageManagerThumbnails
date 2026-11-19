using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

/// <summary>
/// Injects a thumbnail to the left of the name of each item in the built-in Package Manager list.
/// Package Manager internals are not public API, so everything is resolved via reflection
/// and fails soft (no thumbnail) if a member can't be found.
/// </summary>
[InitializeOnLoad]
static class PackageManagerThumbnails
{
    const string WindowTypeName = "UnityEditor.PackageManager.UI.PackageManagerWindow";
    const string ItemTypeName = "PackageItem";
    const string ThumbName = "hs-package-thumbnail";
    const string SpinnerName = "hs-thumb-spinner";
    const string ControlsName = "hs-thumb-controls";
    const string StyledMarker = "hs-styled";
    const string HookedMarker = "hs-list-hooked";
    const string EnabledKey = "HS_PackageThumbs_Enabled";
    const string SizeKey = "HS_PackageThumbs_Size";
    const string MultilineKey = "HS_PackageThumbs_Multiline";
    const string MultilineButtonName = "hs-multiline-button";
    const float MinSize = 32f;
    const float MaxSize = 128f;
    const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Row height == thumbnail height == thumbnail width (kept square).
    static bool s_Enabled = EditorPrefs.GetBool(EnabledKey, true);
    static bool s_Multiline = EditorPrefs.GetBool(MultilineKey, true);
    static float s_Size = Mathf.Clamp(EditorPrefs.GetFloat(SizeKey, 48f), MinSize, MaxSize);
    static float s_OriginalRowHeight = 25f;
    static bool s_RowHeightChanged;

    static readonly Dictionary<string, Texture2D> s_Cache = new Dictionary<string, Texture2D>();
    static readonly HashSet<string> s_Loading = new HashSet<string>();
    static readonly HashSet<string> s_Failed = new HashSet<string>();
    static double s_NextTick;
    static bool s_LoggedFailure;

    static PackageManagerThumbnails()
    {
        EditorApplication.update += Tick;
    }

    static void Tick()
    {
        var now = EditorApplication.timeSinceStartup;
        if (now < s_NextTick)
            return;

        // Scan fast while the mouse is over Package Manager (that's when you scroll or drag the slider),
        // slowly otherwise. This doesn't depend on any of the list's own callbacks.
        var hovered = EditorWindow.mouseOverWindow;
        var hot = hovered != null && hovered.GetType().FullName == WindowTypeName;
        s_NextTick = now + (hot ? 0.06 : 0.5);

        // Both lookups are expensive (FindType walks every assembly, FindObjectsOfTypeAll scans all
        // objects), so cache them and refresh the window list only once a second.
        s_WindowType ??= FindType(WindowTypeName);
        if (s_WindowType == null)
            return;

        if (now >= s_WindowsRefreshAt)
        {
            s_WindowsRefreshAt = now + 1.0;
            s_Windows.Clear();
            foreach (var obj in Resources.FindObjectsOfTypeAll(s_WindowType))
                if (obj is EditorWindow w)
                    s_Windows.Add(w);
        }

        foreach (var window in s_Windows)
        {
            if (window != null && window.rootVisualElement != null)
            {
                EnsureControls(window.rootVisualElement);
                Process(window.rootVisualElement);
            }
        }
    }

    static Type s_WindowType;
    static readonly List<EditorWindow> s_Windows = new List<EditorWindow>();
    static double s_WindowsRefreshAt;

    // Per-row record of what size we last styled it for, so unchanged rows can be skipped quickly.
    class RowState { public float Size; public bool Multiline; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VisualElement, RowState> s_RowState =
        new System.Runtime.CompilerServices.ConditionalWeakTable<VisualElement, RowState>();

    // Eye toggle + size slider, pinned to the bottom of the sidebar (My Assets, Unity Registry, ...).
    // The sidebar is a pane of a TwoPaneSplitView, so we can't add siblings next to it; instead the
    // controls are attached to the sidebar's own hierarchy and positioned absolutely at its bottom.
    static void EnsureControls(VisualElement root)
    {
        var sidebar = root.Q("sidebar");
        if (sidebar == null)
            return;

        var existing = root.Q(ControlsName);
        if (existing != null)
        {
            if (existing.parent == sidebar && existing.Q(MultilineButtonName) != null)
                return;
            existing.RemoveFromHierarchy(); // left over from an older version of the controls
        }

        var box = new VisualElement { name = ControlsName };
        box.style.position = Position.Absolute;
        box.style.left = 0;
        box.style.right = 0;
        box.style.bottom = 0;
        box.style.flexDirection = FlexDirection.Row;
        box.style.alignItems = Align.Center;
        box.style.paddingLeft = box.style.paddingRight = 6;
        box.style.paddingTop = box.style.paddingBottom = 4;

        var eye = new Button { tooltip = "Show/hide asset thumbnails" };
        var iconTex = EditorGUIUtility.IconContent("animationvisibilitytoggleon").image as Texture2D;
        var icon = new Image { image = iconTex };
        icon.style.width = 16;
        icon.style.height = 16;
        if (iconTex != null)
            eye.Add(icon);
        else
            eye.text = "Img";
        eye.style.width = 26;
        eye.style.height = 20;
        eye.style.paddingLeft = eye.style.paddingRight = 0;
        eye.style.marginLeft = eye.style.marginRight = 0;
        eye.style.alignItems = Align.Center;
        eye.style.justifyContent = Justify.Center;

        // Hamburger button: toggles wrapping long names onto multiple lines. The icon is three drawn bars.
        var wrap = new Button { name = MultilineButtonName, tooltip = "Wrap long names onto multiple lines" };
        wrap.style.width = 26;
        wrap.style.height = 20;
        wrap.style.paddingLeft = wrap.style.paddingRight = 0;
        wrap.style.marginLeft = 4;
        wrap.style.marginRight = 0;
        wrap.style.alignItems = Align.Center;
        wrap.style.justifyContent = Justify.Center;
        var bars = new VisualElement();
        bars.style.width = 14;
        bars.style.height = 10;
        bars.style.justifyContent = Justify.SpaceBetween;
        bars.pickingMode = PickingMode.Ignore;
        for (var i = 0; i < 3; i++)
        {
            var bar = new VisualElement();
            bar.style.height = 2;
            bar.style.backgroundColor = EditorGUIUtility.isProSkin ? new Color(0.85f, 0.85f, 0.85f) : new Color(0.2f, 0.2f, 0.2f);
            bar.pickingMode = PickingMode.Ignore;
            bars.Add(bar);
        }
        wrap.Add(bars);

        var slider = new Slider(MinSize, MaxSize) { value = s_Size, tooltip = "Thumbnail height" };
        slider.style.flexGrow = 1;
        slider.style.minWidth = 30;
        slider.style.marginLeft = 6;
        slider.style.marginRight = 0;
        slider.style.marginTop = slider.style.marginBottom = 0;

        void Refresh()
        {
            eye.style.backgroundColor = s_Enabled ? new StyleColor(new Color(1f, 1f, 1f, 0.15f)) : new StyleColor(StyleKeyword.Null);
            icon.style.opacity = s_Enabled ? 1f : 0.5f;
            slider.SetEnabled(s_Enabled);
            wrap.SetEnabled(s_Enabled);
            wrap.style.backgroundColor = s_Multiline ? new StyleColor(new Color(1f, 1f, 1f, 0.15f)) : new StyleColor(StyleKeyword.Null);
            bars.style.opacity = s_Multiline ? 1f : 0.5f;
        }

        eye.clicked += () =>
        {
            s_Enabled = !s_Enabled;
            EditorPrefs.SetBool(EnabledKey, s_Enabled);
            Refresh();
            s_NextTick = 0;
        };
        wrap.clicked += () =>
        {
            s_Multiline = !s_Multiline;
            EditorPrefs.SetBool(MultilineKey, s_Multiline);
            Refresh();
            s_NextTick = 0;
        };
        slider.RegisterValueChangedCallback(e =>
        {
            s_Size = e.newValue;
            EditorPrefs.SetFloat(SizeKey, s_Size);
            s_NextTick = 0; // apply on the next update, live while dragging
        });

        box.Add(eye);
        box.Add(wrap);
        box.Add(slider);
        Refresh();
        sidebar.hierarchy.Add(box);
    }

    static void Process(VisualElement root)
    {
        var items = new List<VisualElement>();
        Collect(root, items);

        foreach (var item in items)
        {
            try { Decorate(item); }
            catch (Exception e)
            {
                if (!s_LoggedFailure)
                {
                    s_LoggedFailure = true;
                    Debug.LogWarning("[PackageManagerThumbnails] " + e.Message);
                }
            }
        }
    }

    static void Collect(VisualElement element, List<VisualElement> result)
    {
        if (element.resolvedStyle.display == DisplayStyle.None)
            return;
        if (element.GetType().Name == ItemTypeName)
        {
            result.Add(element);
            return;
        }
        foreach (var child in element.hierarchy.Children())
            Collect(child, result);
    }

    // Undo everything Decorate did to an item, and put the list's row height back.
    static void Restore(VisualElement item)
    {
        item.Q<Image>(ThumbName)?.RemoveFromHierarchy();
        if (!item.ClassListContains(StyledMarker))
            return;
        item.RemoveFromClassList(StyledMarker);

        item.style.paddingLeft = StyleKeyword.Null;
        item.style.marginLeft = StyleKeyword.Null;
        var main = item.Q("mainItem");
        if (main != null)
        {
            main.style.height = StyleKeyword.Null;
            main.style.flexGrow = StyleKeyword.Null;
            main.style.paddingLeft = StyleKeyword.Null;
            main.style.marginLeft = StyleKeyword.Null;
        }
        var left = item.Q("leftContainer");
        if (left != null)
        {
            left.style.alignSelf = StyleKeyword.Null;
            left.style.minWidth = StyleKeyword.Null;
            left.style.paddingLeft = StyleKeyword.Null;
            left.style.marginLeft = StyleKeyword.Null;
        }
        ClearHover(item);

        var typeIcon = item.Q("packageTypeIcon");
        if (typeIcon != null)
            typeIcon.style.display = StyleKeyword.Null;
        var name = item.Q("packageName");
        if (name != null)
        {
            name.style.marginLeft = StyleKeyword.Null;
            name.style.whiteSpace = StyleKeyword.Null;
            name.style.textOverflow = StyleKeyword.Null;
            name.style.flexShrink = StyleKeyword.Null;
            name.style.minWidth = StyleKeyword.Null;
        }

        item.style.height = StyleKeyword.Null;

        // Back to the original fixed-height virtualization (this rebuilds the list once).
        var list = item.GetFirstAncestorOfType<BaseVerticalCollectionView>();
        if (list != null && s_RowHeightChanged && list.virtualizationMethod != CollectionVirtualizationMethod.FixedHeight)
        {
            list.fixedItemHeight = s_OriginalRowHeight;
            list.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
        }
    }

    static void Decorate(VisualElement item)
    {
        if (!s_Enabled)
        {
            Restore(item);
            return;
        }

        var url = GetImageUrl(GetMember(item, "package"));
        // Unity blocks plain-HTTP requests; the Asset Store CDN serves the same images over HTTPS.
        if (url != null && url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url.Substring("http://".Length);
        var thumb = item.Q<Image>(ThumbName);

        if (string.IsNullOrEmpty(url))
        {
            thumb?.RemoveFromHierarchy();
            return;
        }

        // Fast path: row already styled for this size, with this asset's image showing.
        if (thumb != null && thumb.image != null && (thumb.userData as string) == url && thumb.parent != null
            && s_RowState.TryGetValue(item, out var state) && Mathf.Approximately(state.Size, s_Size)
            && state.Multiline == s_Multiline)
            return;

        var nameLabel = item.Q<Label>("packageName") ?? item.Q<Label>();
        if (nameLabel == null || nameLabel.parent == null)
            return;

        // Item layout: PackageItem > #mainItem (row) > #leftContainer (row) > #packageName.
        // The thumbnail goes first in #leftContainer.
        if (thumb != null && (thumb.parent != nameLabel.parent || nameLabel.parent.IndexOf(thumb) != 0))
        {
            thumb.RemoveFromHierarchy();
            thumb = null;
        }

        if (thumb == null)
        {
            // ScaleAndCrop fills the square and crops the overflow, keeping the center visible.
            thumb = new Image { name = ThumbName, scaleMode = ScaleMode.ScaleAndCrop };
            thumb.style.height = StyleKeyword.Auto;
            thumb.style.marginLeft = 0;
            thumb.style.marginRight = 8;
            thumb.style.flexShrink = 0;
            thumb.style.alignSelf = Align.Stretch; // full row height, top to bottom

            // Index 0: before #packageTypeIcon, which otherwise leaves an empty gap on the left.
            nameLabel.parent.Insert(0, thumb);
        }

        thumb.style.marginLeft = 0; // flush with the row's left edge (also fixes thumbnails created earlier)
        thumb.style.marginTop = thumb.style.marginBottom = -1; // closes a 1px gap at the top and bottom
        thumb.style.width = s_Size; // as wide as the row is tall, so it stays square
        ApplyRowLayout(item, nameLabel);
        RegisterHover(item);
        var rowState = s_RowState.GetOrCreateValue(item);
        rowState.Size = s_Size;
        rowState.Multiline = s_Multiline;

        // The list uses fixed 25px rows. Switch it (once) to dynamic-height virtualization so each row
        // sizes itself; the slider then only changes the rows' own heights, with no list-wide height to
        // keep in sync (which left rows stuck at different sizes).
        var list = item.GetFirstAncestorOfType<BaseVerticalCollectionView>();
        if (list != null && list.virtualizationMethod != CollectionVirtualizationMethod.DynamicHeight)
        {
            if (!s_RowHeightChanged)
            {
                s_OriginalRowHeight = list.fixedItemHeight;
                s_RowHeightChanged = true;
            }
            list.virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight;
        }

        // Hook row creation/binding so rows scrolled into view are sized and decorated immediately,
        // instead of waiting for the next scan (which made them pop to the right size a moment late).
        // Setting makeItem/bindItem rebuilds the list once. The scan stays as a fallback.
        if (list != null && !list.ClassListContains(HookedMarker))
        {
            list.AddToClassList(HookedMarker);
            // makeItem/bindItem only exist on ListView/TreeView (the base class throws).
            var listView = list as ListView;
            var make = listView?.makeItem;
            var bind = listView?.bindItem;
            if (make != null)
                listView.makeItem = () =>
                {
                    var created = make();
                    PreStyle(created);
                    return created;
                };
            if (bind != null)
                listView.bindItem = (bound, index) =>
                {
                    bind(bound, index);
                    try { Decorate(bound); } catch (Exception) { /* the scan will retry */ }
                };

            // On every scroll (wheel or scrollbar), fix up the visible rows straight away. The list's own
            // handler was registered first, so by the time this runs the rows are already recycled/bound.
            var scroller = list.Q<ScrollView>()?.verticalScroller;
            if (scroller != null)
                scroller.valueChanged += _ =>
                {
                    DecorateAll(list);
                    list.schedule.Execute(() => DecorateAll(list)); // again next frame for late binds
                };
        }

        if ((thumb.userData as string) != url)
        {
            thumb.userData = url;
            thumb.image = null;
        }

        if (thumb.image == null)
        {
            if (s_Cache.TryGetValue(url, out var tex) && tex != null)
                thumb.image = tex;
            else
                Load(url);
        }
        UpdatePlaceholder(thumb, url);
    }

    // Gray box with a spinning ring while the image is loading; nothing once it has loaded.
    // If the download failed, the gray box stays but the spinner stops.
    static void UpdatePlaceholder(Image thumb, string url)
    {
        var spinner = thumb.Q(SpinnerName);
        if (thumb.image != null)
        {
            thumb.style.backgroundColor = StyleKeyword.Null;
            if (spinner != null)
                SetSpinnerRunning(spinner, false);
            return;
        }

        thumb.style.backgroundColor = new Color(0.35f, 0.35f, 0.35f);
        thumb.style.alignItems = Align.Center;
        thumb.style.justifyContent = Justify.Center;
        if (spinner == null)
            spinner = CreateSpinner(thumb);
        SetSpinnerRunning(spinner, !s_Failed.Contains(url));
    }

    static VisualElement CreateSpinner(VisualElement parent)
    {
        var ring = new VisualElement { name = SpinnerName, pickingMode = PickingMode.Ignore };
        ring.style.width = 18;
        ring.style.height = 18;
        ring.style.borderTopWidth = ring.style.borderRightWidth = ring.style.borderBottomWidth = ring.style.borderLeftWidth = 2;
        var faint = new Color(1f, 1f, 1f, 0.15f);
        ring.style.borderRightColor = ring.style.borderBottomColor = ring.style.borderLeftColor = faint;
        ring.style.borderTopColor = new Color(1f, 1f, 1f, 0.85f);
        var round = new StyleLength(new Length(50, LengthUnit.Percent));
        ring.style.borderTopLeftRadius = ring.style.borderTopRightRadius =
            ring.style.borderBottomLeftRadius = ring.style.borderBottomRightRadius = round;

        var angle = 0f;
        // Kept in userData so it can be paused: hidden spinners shouldn't keep ticking.
        ring.userData = ring.schedule.Execute(() =>
        {
            angle = (angle + 12f) % 360f;
            ring.style.rotate = new Rotate(Angle.Degrees(angle));
        }).Every(16);
        parent.Add(ring);
        return ring;
    }

    static void SetSpinnerRunning(VisualElement spinner, bool running)
    {
        spinner.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;
        if (spinner.userData is IVisualElementScheduledItem tick)
        {
            if (running) tick.Resume();
            else tick.Pause();
        }
    }

    static void DecorateAll(VisualElement root)
    {
        var rows = new List<VisualElement>();
        Collect(root, rows);
        foreach (var row in rows)
        {
            try { Decorate(row); } catch (Exception) { /* the scan will retry */ }
        }
    }

    // Size a freshly created row before it is ever laid out, so it never appears at the default height.
    static void PreStyle(VisualElement row)
    {
        if (!s_Enabled)
            return;
        var nameLabel = row.Q<Label>("packageName");
        if (nameLabel != null)
            ApplyRowLayout(row, nameLabel);
    }

    // Give the row and #mainItem (the selection/hover background) the slider's height, and make
    // #leftContainer fill it, instead of the 25px of text height they get by default.
    static void ApplyRowLayout(VisualElement item, VisualElement nameLabel)
    {
        item.AddToClassList(StyledMarker);
        var main = item.Q("mainItem");
        item.style.height = s_Size;
        if (main != null)
            main.style.height = s_Size;
        // Remove left padding/margin so the thumbnail sits flush with the row's left edge.
        item.style.paddingLeft = 0;
        item.style.marginLeft = 0;
        if (main != null)
        {
            main.style.paddingLeft = 0;
            main.style.marginLeft = 0;
        }
        // The (empty) type-icon label sits between the thumbnail and the name and would widen the gap.
        var typeIcon = item.Q("packageTypeIcon");
        if (typeIcon != null)
            typeIcon.style.display = DisplayStyle.None;
        nameLabel.style.marginLeft = 0;
        // Multiline: wrap long names onto extra lines. Otherwise use the stock single line with "...".
        if (s_Multiline)
        {
            nameLabel.style.whiteSpace = WhiteSpace.Normal;
            nameLabel.style.textOverflow = TextOverflow.Clip;
        }
        else
        {
            nameLabel.style.whiteSpace = StyleKeyword.Null;
            nameLabel.style.textOverflow = StyleKeyword.Null;
        }
        nameLabel.style.flexShrink = 1;
        nameLabel.style.minWidth = 0;
        if (nameLabel.parent != null)
        {
            nameLabel.parent.style.minWidth = 0;
            nameLabel.parent.style.alignSelf = Align.Stretch;
            nameLabel.parent.style.paddingLeft = 0;
            nameLabel.parent.style.marginLeft = 0;
        }
    }

    static void RegisterHover(VisualElement item)
    {
        const string marker = "hs-hover-registered";
        if (item.ClassListContains(marker))
            return;
        item.AddToClassList(marker);

        item.RegisterCallback<PointerEnterEvent>(_ =>
        {
            var main = item.Q("mainItem");
            if (s_Enabled && main != null && !main.ClassListContains("selected"))
                main.style.backgroundColor = EditorGUIUtility.isProSkin
                    ? new Color(1f, 1f, 1f, 0.08f)
                    : new Color(0f, 0f, 0f, 0.07f);
        });
        item.RegisterCallback<PointerLeaveEvent>(_ => ClearHover(item));
        item.RegisterCallback<PointerDownEvent>(_ => ClearHover(item));
    }

    static void ClearHover(VisualElement item)
    {
        var main = item.Q("mainItem");
        if (main != null)
            main.style.backgroundColor = StyleKeyword.Null;
    }

    static void Load(string url)
    {
        if (s_Failed.Contains(url) || !s_Loading.Add(url))
            return;

        var request = UnityWebRequestTexture.GetTexture(url);
        var op = request.SendWebRequest();
        op.completed += _ =>
        {
            s_Loading.Remove(url);
            if (request.result == UnityWebRequest.Result.Success)
                s_Cache[url] = DownloadHandlerTexture.GetContent(request);
            else
            {
                s_Failed.Add(url);
                Debug.LogWarning($"[PackageManagerThumbnails] Failed to load {url}: {request.error}");
            }
            request.Dispose();
            s_NextTick = 0; // refresh immediately
        };
    }

    // package -> product -> images -> prefer Main image, else first; thumbnailUrl then url.
    static string GetImageUrl(object package)
    {
        if (package == null)
            return null;

        var images = GetMember(GetMember(package, "product"), "images") as IEnumerable;
        if (images == null)
            return null;

        string first = null;
        foreach (var image in images)
        {
            var u = GetMember(image, "thumbnailUrl") as string;
            if (string.IsNullOrEmpty(u))
                u = GetMember(image, "url") as string;
            if (string.IsNullOrEmpty(u))
                continue;

            if (GetMember(image, "type")?.ToString() == "Main")
                return u;
            first ??= u;
        }
        return first;
    }

    static object GetMember(object target, string name)
    {
        if (target == null)
            return null;
        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            var p = t.GetProperty(name, AnyInstance | BindingFlags.DeclaredOnly);
            if (p != null && p.GetIndexParameters().Length == 0)
                return p.GetValue(target);
            var f = t.GetField(name, AnyInstance | BindingFlags.DeclaredOnly);
            if (f != null)
                return f.GetValue(target);
        }
        return null;
    }

    static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName, false);
            if (t != null)
                return t;
        }
        return null;
    }

}
