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
        MarkLayout();
    }

    private void ApplyLayout(string key)
    {
        _settings.SpeakersLayout = key;
        _settings.SpeakerPositions.Clear(); // a fresh arrangement starts from nobody pulled out
        _settings.SpeakersX = _settings.SpeakersY = null; // back to the layout's own spot
        if (_settings.SpeakersCorner == 0) _settings.SpeakersCorner = 1; // picking a layout means "show it"
        _settings.Save();
        SpeakersBtn.Text = "КТО ГОВОРИТ: " + CornerNames[_settings.SpeakersCorner];
        MarkLayout();
        Audio.Sfx.Play("tab");
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
            del.Click += (_, _) => { _settings.SpeakerLayouts.Remove(preset); _settings.Save(); BuildMyLayouts(); };
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

    private void ApplyMyLayout(SpeakerLayoutPreset p)
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
        Audio.Sfx.Play("tab");
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
