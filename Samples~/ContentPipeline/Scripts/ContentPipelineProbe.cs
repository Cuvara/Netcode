using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cuvara.Netcode.Content;
using Shared.GameLogic.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Cuvara.Netcode.Samples.ContentPipeline
{
    /// <summary>
    /// Drives the content pipeline and shows the result. This is the whole behaviour of
    /// <c>ContentPipelineTest.unity</c> — the scene exists to make the pipeline something
    /// you can watch rather than something a test suite asserts about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Point <see cref="serverUrl"/> at a running game server's metrics origin and press
    /// play. The chip at the top is the part worth watching: the first run reads
    /// <b>NETWORK</b>, the second reads <b>CACHE</b>, because the server answered 304 and
    /// no body crossed the wire.
    /// </para>
    /// <para>
    /// Built in UXML rather than IMGUI. Sample and test scenes are where the UI layer gets
    /// exercised and where anyone reading a package learns the intended pattern, so a probe
    /// that reached for <c>OnGUI</c> would both teach the wrong thing and leave the real UI
    /// layer untested.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ContentPipelineProbe : MonoBehaviour
    {
        [Header("Server")]
        [Tooltip("Game server metrics origin. The /content path is appended.")]
        public string serverUrl = "http://127.0.0.1:9100";

        [Tooltip("Fetch on Start. Turn off to drive it from the button instead.")]
        public bool fetchOnStart = true;

        [Header("Offline fallback")]
        [Tooltip("Used when no server answers, so the scene still shows something.")]
        [TextArea(3, 10)]
        public string fallbackJson =
            "{\"items\":[{\"id\":\"offline_stub\",\"name\":\"Offline Stub\",\"slot\":\"none\"," +
            "\"rarity\":\"common\",\"stackMax\":1,\"attack\":0,\"defense\":0,\"levelRequirement\":0}]}";

        readonly ContentClient _content = new ContentClient();
        readonly List<ItemDefinition> _rows = new List<ItemDefinition>();

        CancellationTokenSource _cts;
        TextField _urlField;
        Label _statusLabel;
        Label _sourceChip;
        Label _summary;
        ListView _list;

        void OnEnable()
        {
            _cts = new CancellationTokenSource();

            var root = GetComponent<UIDocument>().rootVisualElement;
            _urlField = root.Q<TextField>("server-url");
            _statusLabel = root.Q<Label>("status");
            _sourceChip = root.Q<Label>("source-chip");
            _summary = root.Q<Label>("summary");
            _list = root.Q<ListView>("items");

            // A Q<T> miss returns null and the scene then fails on first interaction with a
            // NullReferenceException pointing at the handler rather than at the renamed
            // element. Saying so here names the actual fault.
            if (_urlField == null || _statusLabel == null || _sourceChip == null ||
                _summary == null || _list == null)
            {
                Debug.LogError(
                    "[ContentProbe] ContentPipelineView.uxml is missing an expected element " +
                    "(server-url, status, source-chip, summary or items). The names in the UXML " +
                    "and the names queried here have to match.");
                return;
            }

            _urlField.value = serverUrl;
            _urlField.RegisterValueChangedCallback(e => serverUrl = e.newValue);

            root.Q<Button>("fetch").clicked += () => Fetch().Forget();
            root.Q<Button>("clear-cache").clicked += ClearCache;

            _list.itemsSource = _rows;
            _list.makeItem = MakeRow;
            _list.bindItem = BindRow;

            Render();

            if (fetchOnStart)
            {
                Fetch().Forget();
            }
        }

        void OnDisable()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        async UniTaskVoid Fetch()
        {
            SetStatus("fetching " + serverUrl.TrimEnd('/') + "/content ...");
            try
            {
                await _content.FetchAsync(serverUrl, _cts.Token);
                SetStatus($"OK — hash {_content.Database.Hash}");
            }
            catch (ContentException ex)
            {
                // Falls back so the scene demonstrates something with no server running. A
                // real client must NOT do this: content it invented is content the server
                // never validated, and every number in it would be a guess shown to a
                // player as fact.
                SetStatus("fetch failed, showing offline fallback — " + ex.Message);
                try
                {
                    _content.LoadFromJson(fallbackJson, "offline");
                }
                catch (ContentException inner)
                {
                    SetStatus("fallback is unusable too — " + inner.Message);
                }
            }

            Render();
        }

        void ClearCache()
        {
            PlayerPrefs.DeleteKey("content.hash");
            PlayerPrefs.DeleteKey("content.json");
            PlayerPrefs.Save();
            SetStatus("cache cleared — the next fetch downloads the full set");
        }

        void SetStatus(string text)
        {
            if (_statusLabel != null) _statusLabel.text = text;
        }

        void Render()
        {
            if (_list == null) return;

            _rows.Clear();
            foreach (ItemDefinition item in _content.Database.Items)
            {
                _rows.Add(item);
            }

            _summary.text = _rows.Count == 1 ? "1 item" : _rows.Count + " items";
            _list.Rebuild();

            _sourceChip.text = _content.Source.ToString().ToUpperInvariant();
            _sourceChip.RemoveFromClassList("cuvara-probe__chip--network");
            _sourceChip.RemoveFromClassList("cuvara-probe__chip--cache");
            _sourceChip.RemoveFromClassList("cuvara-probe__chip--local");

            switch (_content.Source)
            {
                case ContentSource.Network: _sourceChip.AddToClassList("cuvara-probe__chip--network"); break;
                case ContentSource.Cache: _sourceChip.AddToClassList("cuvara-probe__chip--cache"); break;
                case ContentSource.Local: _sourceChip.AddToClassList("cuvara-probe__chip--local"); break;
            }
        }

        static VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("cuvara-probe__item");

            var name = new Label { name = "name" };
            name.AddToClassList("cuvara-probe__item-name");

            var detail = new Label { name = "detail" };
            detail.AddToClassList("cuvara-probe__item-detail");

            row.Add(name);
            row.Add(detail);
            return row;
        }

        void BindRow(VisualElement element, int index)
        {
            ItemDefinition item = _rows[index];
            element.Q<Label>("name").text = item.Name;
            element.Q<Label>("detail").text = Describe(item);
        }

        static string Describe(ItemDefinition item)
        {
            var sb = new StringBuilder();
            sb.Append(item.Id).Append("  ·  ").Append(item.Rarity).Append("  ·  ");
            sb.Append(item.IsEquippable ? item.Slot.ToString() : "not equippable");
            sb.Append("  ·  stack ").Append(item.StackMax);

            if (item.Attack != 0) sb.Append("  ·  atk ").Append(item.Attack);
            if (item.Defense != 0) sb.Append("  ·  def ").Append(item.Defense);
            if (item.LevelRequirement > 0) sb.Append("  ·  lvl ").Append(item.LevelRequirement);

            return sb.ToString();
        }
    }
}
