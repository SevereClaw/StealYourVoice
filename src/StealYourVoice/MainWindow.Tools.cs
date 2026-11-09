using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StealYourVoice.Audio;
using StealYourVoice.Controls;

namespace StealYourVoice;

/// <summary>Auto-calibration and diagnostics.</summary>
public partial class MainWindow
{
    private enum ToolMode { None, Calib, Diag }
    private enum Phase { Idle, Silence, Speech }

    private const double SilenceSeconds = 3;
    private const double SpeechSeconds = 5;
    private const double DiagSeconds = 3;

    private ToolMode _toolMode = ToolMode.None;
    private Phase _phase = Phase.Idle;
    private DateTime _phaseStart;
    private readonly MicAnalyzer _analyzer = new();

    private void InitTools()
    {
        CalibBtn.Click += _ => StartCalibration();
        DiagBtn.Click += _ => StartDiagnostics();
    }

    /// <summary>Called from the main 30 fps timer.</summary>
    private void ToolTick()
    {
        if (_toolMode == ToolMode.None || _phase == Phase.Idle) return;

        var (l, r) = _vm.StripChannels(_strip, 0); // pre-fader = raw input from the interface
        ToolMeter.Push(Math.Max(l, r));
        var elapsed = (DateTime.Now - _phaseStart).TotalSeconds;

        if (_phase == Phase.Silence)
        {
            _analyzer.AddSilence(l, r);
            ToolStep.Text = $"МОЛЧИ... {Math.Ceiling(SilenceSeconds - elapsed):0}";
            if (elapsed >= SilenceSeconds)
            {
                _phase = Phase.Speech;
                _phaseStart = DateTime.Now;
                ToolSub.Text = "Говори как обычно в игре, с нормальной громкостью. Например, прочитай вслух: " +
                               "«Я пришёл забрать твой голос. Сегодня ночью он станет моим».";
            }
            return;
        }

        var total = _toolMode == ToolMode.Calib ? SpeechSeconds : DiagSeconds;
        _analyzer.AddSpeech(l, r);
        ToolStep.Text = _toolMode == ToolMode.Calib
            ? $"ГОВОРИ! {Math.Ceiling(total - elapsed):0}"
            : $"СКАЖИ ЧТО-НИБУДЬ... {Math.Ceiling(total - elapsed):0}";

        if (elapsed >= total)
        {
            _phase = Phase.Idle;
            if (_toolMode == ToolMode.Calib) ShowCalibResult();
            else ShowDiagResult();
        }
    }

    private void OpenTool(ToolMode mode, string title)
    {
        _toolMode = mode;
        _analyzer.Reset();
        ToolTitle.Text = title;
        ToolBody.Children.Clear();
        ToolButtons.Children.Clear();
        ToolMeter.Visibility = Visibility.Visible;
        AddToolButton("ОТМЕНА", CloseTool);
        ToolOverlay.Visibility = Visibility.Visible;
    }

    private void CloseTool()
    {
        _toolMode = ToolMode.None;
        _phase = Phase.Idle;
        ToolOverlay.Visibility = Visibility.Collapsed;
    }

    // ───────────────────────── calibration ─────────────────────────

    private void StartCalibration()
    {
        if (!_vm.IsConnected) return;
        OpenTool(ToolMode.Calib, "АВТОНАСТРОЙКА");
        ToolSub.Text = "Сядь как обычно, микрофон на своём месте. Сейчас замеряю шум комнаты: кулеры, улицу, гул.";
        _phase = Phase.Silence;
        _phaseStart = DateTime.Now;
    }

    private void ShowCalibResult()
    {
        var rep = _analyzer.Analyze();
        ToolBody.Children.Clear();
        ToolButtons.Children.Clear();
        ToolMeter.Visibility = Visibility.Collapsed;

        if (!rep.HeardVoice)
        {
            ToolStep.Text = "НЕ УСЛЫШАЛ ГОЛОС";
            ToolSub.Text = "С интерфейса почти не идёт сигнал, поэтому настраивать нечего.";
            AddRow(Sev.Fail, "Проверь: кнопка 48V на UMC22 включена, ручка GAIN не на нуле, кабель XLR вставлен до щелчка, " +
                             "в «Устройство» выбран твой Behringer, а говоришь ты в бок B-1 (сторона с логотипом).");
            AddToolButton("ЕЩЁ РАЗ", StartCalibration);
            AddToolButton("ДИАГНОСТИКА", StartDiagnostics);
            AddToolButton("ЗАКРЫТЬ", CloseTool);
            return;
        }

        bool currentMono = _vm.Get(S("Mono")) >= 0.5f;
        var gate = rep.SuggestGate();
        var comp = rep.SuggestComp();
        var gain = rep.SuggestGain(comp);
        bool mono = currentMono || rep.OneSided != null;

        ToolStep.Text = rep.Clipping ? "ЕСТЬ ПЕРЕГРУЗ" : rep.TooQuiet ? "ТИХОВАТО" : "ГОТОВО";
        ToolSub.Text = "Вот что я услышал и что предлагаю выставить:";

        // input level (hardware gain)
        if (rep.Clipping)
            AddRow(Sev.Fail, $"Перегруз на входе (пик {rep.MaxPeak:0.0} dB): звук хрипит, и программно это не починить. " +
                             "Убавь GAIN на UMC22 примерно на 1/8 оборота и запусти автонастройку ещё раз.");
        else if (rep.TooQuiet)
            AddRow(Sev.Warn, $"Тихо на входе (пики {rep.VoicePeak:0} dB). Прибавь GAIN на UMC22: лучше всего, " +
                             "когда пики около −12…−6 dB. Тогда шума будет меньше, чем при усилении в программе.");
        else
            AddRow(Sev.Ok, $"Уровень с UMC22 в норме: пики {rep.VoicePeak:0} dB, запас до перегруза есть.");

        // noise
        var snrText = $"Шум комнаты {rep.Noise:0} dB, голос громче шума на {rep.Snr:0} dB.";
        if (rep.Snr >= 25) AddRow(Sev.Ok, snrText + " Отлично.");
        else if (rep.Snr >= 15) AddRow(Sev.Warn, snrText + " Шумновато: шумодав поможет, но лучше говорить ближе к микрофону (10–15 см).");
        else AddRow(Sev.Fail, snrText + " Очень шумно: говори ближе к микрофону, прибавь GAIN и убери источник шума.");

        if (rep.OneSided != null)
            AddRow(Sev.Warn, $"Голос идёт только в {(rep.OneSided == "L" ? "левый" : "правый")} канал. Включу MONO.");

        AddRow(Sev.Info,
            $"Будет выставлено:  громкость {gain:+0.0;-0.0;0} dB  •  шумодав {gate:0.#}  •  компрессор {comp:0.#}" +
            (mono ? "  •  MONO" : ""));

        AddToolButton("ПРИМЕНИТЬ", () =>
        {
            _vm.Script($"{S("Gain")}={VmClient.F(gain)};{S("Gate")}={VmClient.F(gate)};" +
                       $"{S("Comp")}={VmClient.F(comp)};{S("Mono")}={(mono ? 1 : 0)};");
            CloseTool();
            StatusText.Text = "АВТОНАСТРОЙКА ПРИМЕНЕНА // ТВОЙ ГОЛОС ТЕПЕРЬ НАШ";
        });
        AddToolButton("ЕЩЁ РАЗ", StartCalibration);
        AddToolButton("ОТМЕНА", CloseTool);
    }

    // ───────────────────────── diagnostics ─────────────────────────

    private void StartDiagnostics()
    {
        if (!_vm.IsConnected) return;
        OpenTool(ToolMode.Diag, "ДИАГНОСТИКА");
        ToolSub.Text = "Скажи пару слов обычным голосом: проверю сигнал. Заодно проверю настройки Voicemeeter и Windows.";
        _phase = Phase.Speech;
        _phaseStart = DateTime.Now;
    }

    private void ShowDiagResult()
    {
        var rep = _analyzer.Analyze();
        ToolBody.Children.Clear();
        ToolButtons.Children.Clear();
        ToolMeter.Visibility = Visibility.Collapsed;
        int fails = 0, warns = 0;
        void Count(Sev s) { if (s == Sev.Fail) fails++; else if (s == Sev.Warn) warns++; }

        // ── Voicemeeter routing ──
        var dev = _vm.GetString(S("device.name"));
        if (string.IsNullOrWhiteSpace(dev))
        {
            Count(Sev.Fail);
            AddRow(Sev.Fail, $"На входе {_strip + 1} не выбрано устройство — Voicemeeter ничего не слышит.",
                "ВЫБРАТЬ", () => { CloseTool(); ShowDeviceMenu(); });
        }
        else AddRow(Sev.Ok, $"Микрофон: {dev}");

        if (_vm.Get(S("Mute")) >= 0.5f)
        {
            Count(Sev.Fail);
            AddRow(Sev.Fail, "Микрофон в MUTE.", "ВКЛЮЧИТЬ", () => _vm.Set(S("Mute"), 0));
        }

        if (_vm.Get(S("B1")) < 0.5f)
        {
            Count(Sev.Fail);
            AddRow(Sev.Fail, "Голос не отправляется в запись: кнопка B1 на входе выключена, ShadowPlay и Discord его не услышат.",
                "ВКЛЮЧИТЬ", () => _vm.Set(S("B1"), 1));
        }

        if (_vm.Get(B1("Mute")) >= 0.5f)
        {
            Count(Sev.Fail);
            AddRow(Sev.Fail, "Выход B1 заглушён: в записи будет тишина.", "СНЯТЬ MUTE", () => _vm.Set(B1("Mute"), 0));
        }

        if (_vm.Get(S("A1")) >= 0.5f && string.IsNullOrWhiteSpace(_vm.GetString("Bus[0].device.name")))
        {
            Count(Sev.Warn);
            AddRow(Sev.Warn, "«Слышать себя» включено, но наушники (A1) не выбраны, поэтому себя не слышно.",
                "ВЫБРАТЬ", () => { CloseTool(); ShowHeadphonesMenu("Выбери наушники"); });
        }

        // ── signal ──
        if (!rep.HeardVoice)
        {
            Count(Sev.Fail);
            AddRow(Sev.Fail, "Голос не слышно на входе. Проверь 48V на UMC22, ручку GAIN, кабель и что говоришь в бок B-1.");
        }
        else
        {
            if (rep.Clipping)
            {
                Count(Sev.Fail);
                AddRow(Sev.Fail, $"Перегруз на входе (пик {rep.MaxPeak:0.0} dB): звук хрипит. Убавь GAIN на UMC22.");
            }
            else if (rep.TooQuiet)
            {
                Count(Sev.Warn);
                AddRow(Sev.Warn, $"Тихо на входе (пики {rep.VoicePeak:0} dB). Прибавь GAIN на UMC22 или запусти автонастройку.");
            }
            else AddRow(Sev.Ok, $"Сигнал с микрофона в норме (пики {rep.VoicePeak:0} dB).");

            if (rep.OneSided != null && _vm.Get(S("Mono")) < 0.5f)
            {
                Count(Sev.Fail);
                AddRow(Sev.Fail, $"Голос только в {(rep.OneSided == "L" ? "левом" : "правом")} канале: в записи он будет в одном ухе.",
                    "ВКЛЮЧИТЬ MONO", () => _vm.Set(S("Mono"), 1));
            }
        }

        // ── Windows ──
        var formats = WindowsAudioChecks.ActiveEndpointFormats();
        if (formats.Count > 0)
        {
            var bad = formats.Where(f => f.SampleRate != 48000).ToList();
            if (bad.Count > 0)
            {
                Count(Sev.Warn);
                var list = string.Join("\n", bad.Select(f => $"   • {f.Name} — {f.SampleRate} Hz ({(f.IsCapture ? "запись" : "воспроизведение")})"));
                AddRow(Sev.Warn, "Не все устройства на 48000 Hz. Из-за разной частоты бывают щелчки и треск. " +
                                 "Свойства устройства → Дополнительно → поставь 48000 Hz:\n" + list,
                    "ОТКРЫТЬ «ЗВУК»", WindowsAudioChecks.OpenSoundPanel);
            }
            else AddRow(Sev.Ok, $"Все активные устройства на 48000 Hz ({formats.Count} шт.)");
        }
        else AddRow(Sev.Info, "Не удалось прочитать частоты устройств Windows. Проверь вручную, что везде стоит 48000 Hz.");

        if (WindowsAudioChecks.MicrophoneBlocked())
        {
            Count(Sev.Fail);
            AddRow(Sev.Fail, "Windows запрещает программам доступ к микрофону.", "ОТКРЫТЬ НАСТРОЙКИ", WindowsAudioChecks.OpenMicPrivacy);
        }
        else AddRow(Sev.Ok, "Доступ к микрофону в Windows разрешён.");

        if (WindowsAudioChecks.DuckingEnabled())
        {
            Count(Sev.Warn);
            AddRow(Sev.Warn, "Windows приглушает игру и музыку, когда ты говоришь в Discord (вкладка «Связь»).",
                "ОТКЛЮЧИТЬ", WindowsAudioChecks.DisableDucking);
        }
        else AddRow(Sev.Ok, "Windows не приглушает звук во время разговора.");

        ToolStep.Text = fails + warns == 0 ? "ВСЁ ЧИСТО" : $"ПРОБЛЕМ: {fails}  •  ПРЕДУПРЕЖДЕНИЙ: {warns}";
        ToolSub.Text = fails + warns == 0
            ? "Цепочка в порядке. Если голос всё равно не нравится, запусти автонастройку."
            : "Жми кнопки справа: большую часть исправлю сам.";

        AddToolButton("ПРОВЕРИТЬ ЕЩЁ РАЗ", StartDiagnostics);
        AddToolButton("АВТОНАСТРОЙКА", StartCalibration);
        AddToolButton("ЗАКРЫТЬ", CloseTool);
    }

    // ───────────────────────── UI helpers ─────────────────────────

    private enum Sev { Ok, Warn, Fail, Info }

    private void AddRow(Sev sev, string text, string? fixLabel = null, Action? fix = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var (tagText, tagBg, tagFg) = sev switch
        {
            Sev.Ok => ("OK", P5.White, P5.Black),
            Sev.Warn => ("!", P5.DarkRed, P5.White),
            Sev.Fail => ("✕", P5.Red, P5.White),
            _ => ("i", P5.Coal, P5.White),
        };
        var tag = new Border
        {
            Background = tagBg,
            Width = 44,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = new SkewTransform(-15, 0),
            Child = new TextBlock
            {
                Text = tagText,
                Foreground = tagFg,
                FontFamily = new FontFamily("Arial Black"),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        grid.Children.Add(tag);

        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = sev == Sev.Ok ? P5.Grey : P5.White,
            FontSize = 14,
            Margin = new Thickness(0, 3, 10, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(tb, 1);
        grid.Children.Add(tb);

        if (fixLabel != null && fix != null)
        {
            var btn = new SlashToggle { Text = fixLabel, IsToggle = false, Height = 32, FontSize = 12, VerticalAlignment = VerticalAlignment.Top };
            btn.Click += _ =>
            {
                fix();
                btn.Text = "ГОТОВО ✓";
                btn.IsToggle = true;
                btn.IsChecked = true;
                btn.IsEnabled = false;
                tb.Foreground = P5.Grey;
            };
            Grid.SetColumn(btn, 2);
            grid.Children.Add(btn);
        }

        ToolBody.Children.Add(grid);
    }

    private void AddToolButton(string text, Action onClick)
    {
        var b = new SlashToggle { Text = text, IsToggle = false, Height = 40, FontSize = 15 };
        b.Click += _ => onClick();
        ToolButtons.Children.Add(b);
    }
}
