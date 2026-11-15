using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using StealYourVoice.Controls;

namespace StealYourVoice;

/// <summary>Ready-made and saved arrangements of the people over the game.</summary>
public partial class MainWindow
{
    private static readonly (string Key, string Text, string Hint)[] Layouts =
    {
        ("list", "СТОЛБИК", "Обычный список в углу (или там, куда ты его перетащил)"),
        ("row-top", "СТРОКА ▲", "Все в ряд по центру вверху экрана"),
        ("row-bottom", "СТРОКА ▼", "Все в ряд по центру внизу экрана"),
        ("edges", "ПО КРАЯМ", "Люди по очереди слева и справа у краёв экрана — центр свободен"),
        ("stairs", "ЛЕСЕНКА", "Ступеньками сверху слева, как меню Persona"),
    };

    private void InitLayouts()
    {
        foreach (var l in Layouts)
        {
            var key = l.Key;
            var b = new SlashToggle { Text = l.Text, Hint = l.Hint, Height = 32, FontSize = 12, Sound = "", Tilt = (key.Length % 3 - 1) * 1.1 };
            b.Click += _ => ApplyLayout(key);
            SpkLayoutPanel.Children.Add(b);
        }
        BuildMyLayouts();
        BuildCountButtons();
        MarkLayout();
    }

    // ───────────────────────── arrangement by the number of people ─────────────────────────

    private int _lastPeopleCount = -1;

    private string LayoutTitle(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "—";
        if (value.StartsWith("my:")) return "★ " + value[3..];
        return Layouts.FirstOrDefault(l => l.Key == value).Text ?? "—";
    }

    private void BuildCountButtons()
    {
        SpkCountPanel.Children.Clear();
        for (int n = 2; n <= 10; n++)
        {
            int count = n;
            _settings.LayoutByCount.TryGetValue(n, out var v);
            var title = LayoutTitle(v);
            if (title.Length > 14) title = title[..13] + "…";
            var b = new SlashToggle
            {
                Text = $"{n}: {title}", IsToggle = false, Height = 30, FontSize = 11, Sound = "",
                Hint = $"Какая расстановка включается, когда в звонке {n} человек (с тобой)"
            };
            b.IsChecked = false;
            b.Click += _ => ShowCountMenu(b, count);
            SpkCountPanel.Children.Add(b);
        }
    }

    private void ShowCountMenu(FrameworkElement target, int count)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string header, string? value)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) =>
            {
                if (value == null) _settings.LayoutByCount.Remove(count);
                else _settings.LayoutByCount[count] = value;
                _settings.Save();
                BuildCountButtons();
                _lastPeopleCount = -1; // re-check right away
                CheckPeopleCount(_dcCurrent.Count + (_dcSelfInChannel ? 1 : 0));
            };
            menu.Items.Add(mi);
        }
        Add("Не менять", null);
        menu.Items.Add(new Separator());
        foreach (var l in Layouts) Add(l.Text, l.Key);
        if (_settings.SpeakerLayouts.Count > 0) menu.Items.Add(new Separator());
        foreach (var p in _settings.SpeakerLayouts) Add("★ " + p.Name, "my:" + p.Name);
        menu.IsOpen = true;
    }

    /// <summary>Called whenever the people list is refreshed: on a new head count, switch arrangements.</summary>
    private void CheckPeopleCount(int count)
    {
        if (count == _lastPeopleCount) return;
        _lastPeopleCount = count;
        if (count < 2 || !_settings.LayoutByCount.TryGetValue(Math.Min(count, 10), out var v)) return;

        if (v.StartsWith("my:"))
        {
            var p = _settings.SpeakerLayouts.FirstOrDefault(x => x.Name == v[3..]);
            if (p != null) ApplyMyLayout(p, quiet: true);
        }
        else if (v != _settings.SpeakersLayout || _settings.SpeakerPositions.Count > 0 || _settings.SpeakersX.HasValue)
            ApplyLayout(v, quiet: true);
    }

    private void ApplyLayout(string key) => ApplyLayout(key, false);

    private void ApplyLayout(string key, bool quiet)
    {
        _settings.SpeakersLayout = key;
        _settings.SpeakerPositions.Clear(); // a fresh arrangement starts from nobody pulled out
        _settings.SpeakersX = _settings.SpeakersY = null; // back to the layout's own spot
        if (_settings.SpeakersCorner == 0) _settings.SpeakersCorner = 1; // picking a layout means "show it"
        _settings.Save();
        SpeakersBtn.Text = "КТО ГОВОРИТ: " + CornerNames[_settings.SpeakersCorner];
        MarkLayout();
        if (!quiet) Audio.Sfx.Play("tab");
        UpdateSpeakers();
    }

    private void MarkLayout()
    {
        int i = 0;
        foreach (var t in SpkLayoutPanel.Children.OfType<SlashToggle>())
            t.IsChecked = Layouts[i++].Key == _settings.SpeakersLayout;
    }

    private void BuildMyLayouts()
    {
        SpkMyLayoutsPanel.Children.Clear();
        foreach (var p in _settings.SpeakerLayouts)
        {
            var preset = p;
            var b = new SlashToggle { Text = "★ " + p.Name, IsToggle = false, Height = 32, FontSize = 12, Sound = "",
                Hint = "Твоя расстановка. ПКМ — перезаписать / удалить" };
            b.Click += _ => ApplyMyLayout(preset);
            var menu = new ContextMenu();
            var over = new MenuItem { Header = "Перезаписать текущей расстановкой" };
            over.Click += (_, _) => { Fill(preset); _settings.Save(); };
            var del = new MenuItem { Header = "Удалить" };
            del.Click += (_, _) =>
            {
                _settings.SpeakerLayouts.Remove(preset);
                foreach (var k in _settings.LayoutByCount.Where(kv => kv.Value == "my:" + preset.Name).Select(kv => kv.Key).ToList())
                    _settings.LayoutByCount.Remove(k);
                _settings.Save();
                BuildMyLayouts();
                BuildCountButtons();
            };
            menu.Items.Add(over);
            menu.Items.Add(del);
            b.ContextMenu = menu;
            SpkMyLayoutsPanel.Children.Add(b);
        }
        var add = new SlashToggle { Text = "+ СОХРАНИТЬ РАССТАНОВКУ", IsToggle = false, Height = 32, FontSize = 12, Tilt = 1,
            Hint = "Запомнить, где сейчас стоят список и вытащенные люди, и их размер" };
        add.Click += _ =>
        {
            var p = new SpeakerLayoutPreset { Name = $"РАССТАНОВКА {_settings.SpeakerLayouts.Count + 1}" };
            Fill(p);
            _settings.SpeakerLayouts.Add(p);
            _settings.Save();
            BuildMyLayouts();
            BuildCountButtons();
            StatusText.Text = $"СОХРАНЕНО: {p.Name}";
        };
        SpkMyLayoutsPanel.Children.Add(add);
    }

    private void Fill(SpeakerLayoutPreset p)
    {
        p.Layout = _settings.SpeakersLayout;
        p.Corner = _settings.SpeakersCorner == 0 ? 1 : _settings.SpeakersCorner;
        p.X = _settings.SpeakersX;
        p.Y = _settings.SpeakersY;
        p.Scale = _settings.SpeakersScale;
        p.Positions = _settings.SpeakerPositions.ToDictionary(kv => kv.Key, kv => (double[])kv.Value.Clone());
    }

    private void ApplyMyLayout(SpeakerLayoutPreset p) => ApplyMyLayout(p, false);

    private void ApplyMyLayout(SpeakerLayoutPreset p, bool quiet)
    {
        _settings.SpeakersLayout = p.Layout;
        _settings.SpeakersCorner = p.Corner;
        _settings.SpeakersX = p.X;
        _settings.SpeakersY = p.Y;
        _settings.SpeakersScale = p.Scale;
        _settings.SpeakerPositions = p.Positions.ToDictionary(kv => kv.Key, kv => (double[])kv.Value.Clone());
        _settings.Save();
        SpkScaleS.Value = Math.Round(p.Scale * 100);
        SpeakersBtn.Text = "КТО ГОВОРИТ: " + CornerNames[_settings.SpeakersCorner];
        MarkLayout();
        if (!quiet) Audio.Sfx.Play("tab");
        UpdateSpeakers();
    }

    /// <summary>Where a person stands in the spread-out layouts (null = they go into the shared list).</summary>
    private Point? LayoutSpot(int index, int count)
    {
        var a = SystemParameters.WorkArea;
        double k = _settings.SpeakersScale;
        switch (_settings.SpeakersLayout)
        {
            case "edges":
            {
                int side = index % 2, row = index / 2, rows = (count + 1) / 2;
                double step = 54 * k;
                double top = a.Top + (a.Height - rows * step) / 2;
                double plate = 230 * k;
                return new Point(side == 0 ? a.Left + 12 : a.Right - plate - 12, top + row * step);
            }
            case "stairs":
                return new Point(a.Left + 14 + index * 34 * k, a.Top + 70 + index * 50 * k);
            default:
                return null;
        }
    }
}
