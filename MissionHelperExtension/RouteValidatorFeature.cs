using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MissionHelperExtension
{
    /// <summary>
    /// Feature: Validates the route and shows a green/red status button.
    /// Rules:
    ///   1. The azimuth (AZ) of the last 4 waypoints must be the same, ignoring the
    ///      3rd waypoint from the end (that one is a command with azimuth 0).
    ///   2. The Frame of every waypoint must be Absolute (not Relative/Terrain).
    ///   3. Every Angle value must be within the range [-2, +2].
    ///   4. The 2nd waypoint must be 9-11 m higher than the TAKEOFF waypoint.
    ///   5. The TAKEOFF waypoint must be 90-110 m higher than home.
    ///   6. No point on the cruise portion may be below 30 m above terrain
    ///      (checked on demand via the Validate button; takeoff/landing excluded).
    /// The offending grid cells are highlighted in red, and validation re-runs when a
    /// value changes or a new mission file is loaded.
    /// </summary>
    public class RouteValidatorFeature
    {
        // Azimuth values are display-rounded, so allow a small tolerance when
        // comparing them for equality (in degrees).
        private const double AzimuthToleranceDeg = 1.0;
        private const double AngleMin = -2.0;
        private const double AngleMax = 2.0;

        // LAND waypoints use a steeper (descending) approach angle, so they get their own range.
        private const double LandAngleMin = -50.0;
        private const double LandAngleMax = -20.0;

        // The first waypoint must be a TAKEOFF with this exact pitch angle (Param1), in degrees.
        private const double TakeoffPitch = 6.0;

        // The TAKEOFF waypoint must be this much higher than home (in metres). Frames are
        // Absolute (enforced by rule 2), so both altitudes are AMSL and the difference is
        // the height above home.
        private const double TakeoffMinAboveHome = 90.0;
        private const double TakeoffMaxAboveHome = 110.0;

        // The 3rd waypoint from the end must be DO_CHANGE_SPEED with this exact speed (Param2).
        private const double ChangeSpeedValue = 42.0;

        // The 2nd waypoint must be this much higher than the TAKEOFF waypoint (in metres).
        private const double SecondWpMinAboveTakeoff = 9.0;
        private const double SecondWpMaxAboveTakeoff = 11.0;

        // Minimum required clearance above the terrain along the cruise portion of the
        // route (in metres), and the spacing at which each leg is sampled.
        private const double MinTerrainClearance = 30.0;
        private const double TerrainSampleSpacingM = 30.0;

        private static readonly Color OkColor = Color.FromArgb(0, 128, 0);
        private static readonly Color FailColor = Color.FromArgb(192, 0, 0);
        private static readonly Color EmptyColor = Color.FromArgb(80, 80, 80);
        private static readonly Color BadCellColor = Color.FromArgb(180, 50, 50);
        private static readonly Color BadCellForeColor = Color.White;

        private readonly DataGridView _grid;
        private readonly Action<string> _log;
        private readonly Panel _hostPanel;

        private Panel _controlPanel;   // only used when we create our own panel
        private Button _validateButton;
        private ToolTip _toolTip;
        private System.Windows.Forms.Timer _watchTimer;

        private readonly List<DataGridViewRow> _highlightedRows = new List<DataGridViewRow>();

        // Results are split into synchronous rules (fast, run inline) and the terrain
        // clearance rule (slow, run on a background task). The button/highlights show the
        // union of both.
        private readonly HashSet<int> _rowsToHighlight = new HashSet<int>();  // sync rows
        private List<string> _syncMessages = new List<string>();
        private List<string> _terrainMessages = new List<string>();
        private HashSet<int> _terrainRows = new HashSet<int>();
        private List<string> _lastMessages = new List<string>();  // combined (for the dialog)
        private string _lastSignature = "";

        // Background terrain scan bookkeeping.
        private string _terrainSig = "";                 // geometry the terrain result reflects
        private CancellationTokenSource _terrainCts;     // cancels a superseded scan
        private bool _terrainScanning;

        /// <param name="hostPanel">
        /// Optional existing panel to place the validator button on (e.g. the copy
        /// feature's control panel) so it shares the same line. If null, the feature
        /// creates its own bottom-docked panel.
        /// </param>
        public RouteValidatorFeature(DataGridView grid, Action<string> log, Panel hostPanel = null)
        {
            _grid = grid;
            _log = log;
            _hostPanel = hostPanel;
        }

        public void Initialize()
        {
            _log("RouteValidatorFeature: Initializing...");

            CreateUI();

            // Instant feedback on user edits / row changes.
            _grid.CellValueChanged += OnGridChanged;
            _grid.RowsAdded += OnGridChanged;
            _grid.RowsRemoved += OnGridChanged;
            // Commit combo/checkbox edits immediately so CellValueChanged fires on change,
            // not only when the cell loses focus.
            _grid.CurrentCellDirtyStateChanged += OnCurrentCellDirtyStateChanged;

            // Safety net: catches mission-file loads and other programmatic repopulation
            // that may not raise the grid events above.
            _watchTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _watchTimer.Tick += OnWatchTick;
            _watchTimer.Start();

            Validate();

            _log("RouteValidatorFeature: Initialized");
        }

        public void Cleanup()
        {
            CancelTerrainScan();

            if (_watchTimer != null)
            {
                _watchTimer.Stop();
                _watchTimer.Tick -= OnWatchTick;
                _watchTimer.Dispose();
            }

            if (_grid != null)
            {
                _grid.CellValueChanged -= OnGridChanged;
                _grid.RowsAdded -= OnGridChanged;
                _grid.RowsRemoved -= OnGridChanged;
                _grid.CurrentCellDirtyStateChanged -= OnCurrentCellDirtyStateChanged;
                ClearHighlights();
            }

            if (_controlPanel != null)
            {
                // We created our own panel.
                _controlPanel.Parent?.Controls.Remove(_controlPanel);
                _controlPanel.Dispose();
            }
            else if (_validateButton != null)
            {
                // Button lives on the shared host panel.
                _validateButton.Parent?.Controls.Remove(_validateButton);
                _validateButton.Dispose();
            }

            _toolTip?.Dispose();
        }

        private void CreateUI()
        {
            _validateButton = new Button
            {
                Text = "Перевірити",
                Width = 140,
                Height = 24,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleCenter
            };
            _validateButton.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 64);
            _validateButton.Click += OnValidateClick;

            _toolTip = new ToolTip
            {
                AutoPopDelay = 15000,
                InitialDelay = 300,
                ReshowDelay = 200
            };

            if (_hostPanel != null)
            {
                PlaceOnHostPanel();
            }
            else
            {
                PlaceOnOwnPanel();
            }

            _log("RouteValidatorFeature: UI created");
        }

        /// <summary>
        /// Share the copy feature's line: dock the button to the right edge of its panel.
        /// </summary>
        private void PlaceOnHostPanel()
        {
            const int margin = 8;

            _hostPanel.SuspendLayout();
            try
            {
                _validateButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                _validateButton.Location = new Point(
                    Math.Max(0, _hostPanel.ClientSize.Width - _validateButton.Width - margin),
                    3);
                _hostPanel.Controls.Add(_validateButton);
                _validateButton.BringToFront();
            }
            finally
            {
                _hostPanel.ResumeLayout(true);
            }
        }

        /// <summary>
        /// Fallback: create our own bottom-docked panel when no host panel is supplied.
        /// </summary>
        private void PlaceOnOwnPanel()
        {
            Control parent = _grid.Parent;
            if (parent == null)
            {
                _log("RouteValidatorFeature: Grid has no parent, cannot create UI");
                return;
            }

            _controlPanel = new Panel
            {
                Height = 30,
                Dock = DockStyle.Bottom,
                Padding = new Padding(5),
                BackColor = Color.FromArgb(38, 38, 38)  // Dark background to match MP
            };

            var label = new Label
            {
                Text = "Маршрут:",
                AutoSize = true,
                Location = new Point(5, 7),
                ForeColor = Color.White
            };

            _validateButton.Location = new Point(55, 3);

            _controlPanel.Controls.Add(label);
            _controlPanel.Controls.Add(_validateButton);

            parent.SuspendLayout();
            try
            {
                parent.Controls.Add(_controlPanel);
                // SendToBack so this bottom-docked panel is laid out before the Fill grid,
                // leaving the grid to occupy the remaining space above it.
                _controlPanel.SendToBack();

                if (_grid.Dock != DockStyle.Fill && _grid.Dock != DockStyle.Bottom)
                {
                    _grid.Height = Math.Max(0, _grid.Height - _controlPanel.Height);
                }
            }
            finally
            {
                parent.ResumeLayout(true);
            }
        }

        private void OnGridChanged(object sender, EventArgs e)
        {
            Validate();
        }

        private void OnCurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            try
            {
                if (_grid.IsCurrentCellDirty)
                    _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
            catch { }
        }

        private void OnWatchTick(object sender, EventArgs e)
        {
            // Only re-validate if the relevant grid contents actually changed (e.g. a
            // mission file was loaded), so we don't repaint the grid every second.
            try
            {
                if (ComputeSignature() != _lastSignature)
                {
                    _log("RouteValidatorFeature: grid change detected (file load / external edit)");
                    Validate();
                }
            }
            catch { }
        }

        private void OnValidateClick(object sender, EventArgs e)
        {
            // Re-run the synchronous rules now; the terrain scan runs continuously in the
            // background, so the dialog shows whatever combined result we have so far.
            Validate();

            var messages = _lastMessages;

            string details;
            MessageBoxIcon icon;
            if (GetEffectiveRowCount() == 0)
            {
                details = "Немає точок для перевірки.";
                icon = MessageBoxIcon.Information;
            }
            else if (messages.Count > 0)
            {
                var numbered = new List<string>();
                for (int i = 0; i < messages.Count; i++)
                    numbered.Add($"{i + 1}. {messages[i]}");
                details = string.Join(Environment.NewLine, numbered);
                icon = MessageBoxIcon.Warning;
            }
            else
            {
                details = "Маршрут дійсний.";
                icon = MessageBoxIcon.Information;
            }

            // Note when the terrain scan is still running so an empty result isn't
            // mistaken for "no terrain problems".
            if (_terrainScanning)
            {
                details += Environment.NewLine + Environment.NewLine +
                           "(Перевірка рельєфу ще триває — результат оновиться автоматично.)";
            }

            MessageBox.Show(details, "Перевірка маршруту", MessageBoxButtons.OK, icon);
        }

        /// <summary>
        /// Runs the synchronous validation rules inline, then (re)starts the background
        /// terrain-clearance scan if the route geometry changed. The button/highlights
        /// always show the union of the synchronous result and the latest terrain result.
        /// </summary>
        private void Validate()
        {
            if (_validateButton == null) return;

            _rowsToHighlight.Clear();
            _syncMessages = new List<string>();

            // Nothing to validate for an empty plan - show a neutral state instead.
            if (GetEffectiveRowCount() == 0)
            {
                CancelTerrainScan();
                _terrainMessages = new List<string>();
                _terrainRows = new HashSet<int>();
                _terrainSig = "";
                _lastMessages = new List<string>();
                ClearHighlights();
                ShowEmptyState();
                _lastSignature = ComputeSignature();
                return;
            }

            int count = GetEffectiveRowCount();
            try
            {
                CheckFirstWaypoint(count, _syncMessages);
                CheckSecondWaypointAltitude(count, _syncMessages);
                CheckThirdFromEnd(count, _syncMessages);
                CheckLastWaypoint(count, _syncMessages);
                CheckAzimuth(count, _syncMessages);
                CheckFrames(count, _syncMessages);
                CheckAngles(count, _syncMessages);
            }
            catch (Exception ex)
            {
                _syncMessages.Add($"Помилка перевірки: {ex.Message}");
                _log($"RouteValidatorFeature: Validate error - {ex.Message}\n{ex.StackTrace}");
            }

            RenderResults();

            // Record the state we just validated so the watch timer can detect real changes.
            _lastSignature = ComputeSignature();

            // Kick off (or refresh) the terrain scan for the current geometry.
            MaybeStartTerrainScan(count);
        }

        /// <summary>
        /// Merges the synchronous and terrain results into the button, tooltip and row
        /// highlights. Safe to call repeatedly (e.g. when the terrain scan completes).
        /// </summary>
        private void RenderResults()
        {
            if (_validateButton == null) return;

            var combined = new List<string>(_syncMessages);
            combined.AddRange(_terrainMessages);
            _lastMessages = combined;

            var rows = new HashSet<int>(_rowsToHighlight);
            rows.UnionWith(_terrainRows);
            ApplyHighlights(rows);

            UpdateButton(combined);
        }

        private void ShowEmptyState()
        {
            if (_validateButton == null) return;

            _validateButton.BackColor = EmptyColor;
            _validateButton.Text = "Немає маршруту";
            _validateButton.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 64);
            _toolTip?.SetToolTip(_validateButton, "Немає точок для перевірки.");
        }

        private void UpdateButton(List<string> messages)
        {
            bool ok = messages.Count == 0;
            string suffix = _terrainScanning ? " …" : "";

            _validateButton.BackColor = ok ? OkColor : FailColor;
            _validateButton.Text = (ok ? "Маршрут OK" : $"Маршрут: {messages.Count} проблем(и)") + suffix;
            _validateButton.FlatAppearance.BorderColor = ok
                ? Color.FromArgb(0, 96, 0)
                : Color.FromArgb(128, 0, 0);

            string tip = ok
                ? "Маршрут дійсний."
                : string.Join(Environment.NewLine, messages);
            if (_terrainScanning)
                tip += (tip.Length > 0 ? Environment.NewLine : "") + "Перевірка рельєфу…";
            _toolTip?.SetToolTip(_validateButton, tip);
        }

        #region Validation rules

        /// <summary>
        /// Rule 0: the first waypoint must be a TAKEOFF command with pitch angle 6 (Param1).
        /// </summary>
        private void CheckFirstWaypoint(int count, List<string> messages)
        {
            if (count < 1) return;

            string cmdCol = FindColumn("Command", "command", "CMD", "cmd");
            if (cmdCol == null)
            {
                messages.Add("Перша точка: стовпець 'Command' не знайдено в таблиці.");
                return;
            }

            string cmd = GetCellString(_grid.Rows[0], cmdCol);
            if (!CommandEquals(cmd, "TAKEOFF"))
            {
                messages.Add($"Перша точка має бути TAKEOFF (знайдено '{(string.IsNullOrEmpty(cmd) ? "порожньо" : cmd)}').");
                _rowsToHighlight.Add(0);
                return;
            }

            // For TAKEOFF the pitch angle is stored in Param1.
            string pitchCol = FindColumn("Param1", "param1", "P1", "p1");
            if (pitchCol == null)
            {
                messages.Add("Перша точка: стовпець 'Param1' (тангаж) не знайдено в таблиці.");
                return;
            }

            double pitch = GetCellDouble(_grid.Rows[0], pitchCol);
            if (pitch != TakeoffPitch)
            {
                messages.Add($"Тангаж першої точки (TAKEOFF) має бути {TakeoffPitch:0.#}° (знайдено {pitch:0.##}°).");
                _rowsToHighlight.Add(0);
            }

            // The TAKEOFF waypoint must be 90-110 m above home.
            string altCol = FindColumn("Alt", "alt", "ALT", "Altitude", "altitude", "Z", "z");
            if (altCol == null)
            {
                messages.Add("Перша точка: стовпець 'Alt' не знайдено в таблиці.");
                return;
            }

            if (!TryGetHomeAltitude(out double homeAlt))
            {
                messages.Add("Висота 1-ї точки: не знайдено висоту дому (поле 'Home Alt' у Flight Planner).");
                return;
            }

            double takeoffAlt = GetCellDouble(_grid.Rows[0], altCol);
            double aboveHome = takeoffAlt - homeAlt;
            if (aboveHome < TakeoffMinAboveHome || aboveHome > TakeoffMaxAboveHome)
            {
                messages.Add(
                    $"Перша точка (TAKEOFF) має бути на {TakeoffMinAboveHome:0.#}-{TakeoffMaxAboveHome:0.#} м вище дому " +
                    $"(зараз {aboveHome:0.#} м: висота {takeoffAlt:0.#} м, дім {homeAlt:0.#} м).");
                _rowsToHighlight.Add(0);
            }
        }

        /// <summary>
        /// Rule 4: the 2nd waypoint must be 9-11 m higher than the TAKEOFF waypoint.
        /// Frames are Absolute, so the grid Alt is AMSL and the difference between the two
        /// waypoint altitudes is the height of WP 2 above the takeoff point.
        /// </summary>
        private void CheckSecondWaypointAltitude(int count, List<string> messages)
        {
            if (count < 2)
            {
                messages.Add($"Висота 2-ї точки: потрібно щонайменше 2 точки (знайдено {count}).");
                return;
            }

            string altCol = FindColumn("Alt", "alt", "ALT", "Altitude", "altitude", "Z", "z");
            if (altCol == null)
            {
                messages.Add("Висота 2-ї точки: стовпець 'Alt' не знайдено в таблиці.");
                return;
            }

            double takeoffAlt = GetCellDouble(_grid.Rows[0], altCol);
            double wpAlt = GetCellDouble(_grid.Rows[1], altCol);
            double aboveTakeoff = wpAlt - takeoffAlt;

            if (aboveTakeoff < SecondWpMinAboveTakeoff || aboveTakeoff > SecondWpMaxAboveTakeoff)
            {
                messages.Add(
                    $"Точка 2 має бути на {SecondWpMinAboveTakeoff:0.#}-{SecondWpMaxAboveTakeoff:0.#} м вище точки зльоту " +
                    $"(зараз {aboveTakeoff:0.#} м: висота {wpAlt:0.#} м, TAKEOFF {takeoffAlt:0.#} м).");
                _rowsToHighlight.Add(1);
            }
        }

        /// <summary>
        /// Rule: the 3rd waypoint from the end must be a DO_CHANGE_SPEED command with
        /// speed value 42 (Param2).
        /// </summary>
        private void CheckThirdFromEnd(int count, List<string> messages)
        {
            if (count < 3)
            {
                messages.Add($"3-я з кінця точка: потрібно щонайменше 3 точки (знайдено {count}).");
                return;
            }

            int idx = count - 3;

            string cmdCol = FindColumn("Command", "command", "CMD", "cmd");
            if (cmdCol == null)
            {
                messages.Add("3-я з кінця точка: стовпець 'Command' не знайдено в таблиці.");
                return;
            }

            string cmd = GetCellString(_grid.Rows[idx], cmdCol);
            if (!CommandEquals(cmd, "DO_CHANGE_SPEED"))
            {
                messages.Add($"Точка {idx + 1} (3-я з кінця) має бути DO_CHANGE_SPEED (знайдено '{(string.IsNullOrEmpty(cmd) ? "порожньо" : cmd)}').");
                _rowsToHighlight.Add(idx);
                return;
            }

            // For DO_CHANGE_SPEED the target speed is stored in Param2.
            string speedCol = FindColumn("Param2", "param2", "P2", "p2");
            if (speedCol == null)
            {
                messages.Add("3-я з кінця точка: стовпець 'Param2' (швидкість) не знайдено в таблиці.");
                return;
            }

            double speed = GetCellDouble(_grid.Rows[idx], speedCol);
            if (speed != ChangeSpeedValue)
            {
                messages.Add($"Точка {idx + 1} (DO_CHANGE_SPEED): швидкість має бути {ChangeSpeedValue:0.#} (знайдено {speed:0.##}).");
                _rowsToHighlight.Add(idx);
            }
        }

        /// <summary>
        /// Rule: the last waypoint must be a LAND command.
        /// </summary>
        private void CheckLastWaypoint(int count, List<string> messages)
        {
            if (count < 1) return;

            string cmdCol = FindColumn("Command", "command", "CMD", "cmd");
            if (cmdCol == null)
            {
                messages.Add("Остання точка: стовпець 'Command' не знайдено в таблиці.");
                return;
            }

            int idx = count - 1;
            string cmd = GetCellString(_grid.Rows[idx], cmdCol);
            if (!CommandEquals(cmd, "LAND"))
            {
                messages.Add($"Остання точка (точка {idx + 1}) має бути LAND (знайдено '{(string.IsNullOrEmpty(cmd) ? "порожньо" : cmd)}').");
                _rowsToHighlight.Add(idx);
            }
        }

        /// <summary>
        /// Rule 1: azimuth of the last 4 waypoints must match, ignoring the 3rd from
        /// the end (a command with azimuth 0). Compares waypoints n-1, n-2 and n-4.
        /// </summary>
        private void CheckAzimuth(int count, List<string> messages)
        {
            if (count < 4)
            {
                messages.Add($"Азимут: потрібно щонайменше 4 точки для перевірки (знайдено {count}).");
                return;
            }

            string azCol = FindColumn("AZ", "az", "Azimuth", "azimuth");
            if (azCol == null)
            {
                messages.Add("Азимут: стовпець 'AZ' не знайдено в таблиці.");
                return;
            }

            // Indices from the end: n-1 (last), n-2, n-3 (ignored command), n-4.
            int iLast = count - 1;
            int iSecond = count - 2;
            int iFourth = count - 4;

            double azRef = GetCellDouble(_grid.Rows[iLast], azCol);
            double azSecond = GetCellDouble(_grid.Rows[iSecond], azCol);
            double azFourth = GetCellDouble(_grid.Rows[iFourth], azCol);

            bool secondOk = AngularDiff(azRef, azSecond) <= AzimuthToleranceDeg;
            bool fourthOk = AngularDiff(azRef, azFourth) <= AzimuthToleranceDeg;

            if (!secondOk || !fourthOk)
            {
                messages.Add(
                    $"Розбіжність азимута на останніх точках (ігноруючи точку {iSecond}): " +
                    $"точка {iFourth + 1}={azFourth:0.#}°, точка {iSecond + 1}={azSecond:0.#}°, точка {iLast + 1}={azRef:0.#}°. " +
                    "Вони мають бути однаковими.");

                _rowsToHighlight.Add(iLast);
                _rowsToHighlight.Add(iSecond);
                _rowsToHighlight.Add(iFourth);
            }
        }

        /// <summary>
        /// Rule 2: every waypoint's Frame must be Absolute.
        /// </summary>
        private void CheckFrames(int count, List<string> messages)
        {
            string frameCol = FindColumn("Frame", "frame", "FRAME");
            if (frameCol == null)
            {
                messages.Add("Стовпець 'Frame' не знайдено в таблиці.");
                return;
            }

            var badRows = new List<int>();
            for (int i = 0; i < count; i++)
            {
                string frame = GetCellString(_grid.Rows[i], frameCol);
                if (!IsAbsoluteFrame(frame))
                {
                    badRows.Add(i + 1);
                    _rowsToHighlight.Add(i);
                }
            }

            if (badRows.Count > 0)
            {
                messages.Add(
                    $"Frame має бути Absolute для всіх точок. Не Absolute у точках: {string.Join(", ", badRows)}.");
            }
        }

        /// <summary>
        /// Rule 3: every Angle value must be within [-2, +2].
        /// </summary>
        private void CheckAngles(int count, List<string> messages)
        {
            string angleCol = FindColumn("Angle", "angle", "ANGLE");
            if (angleCol == null)
            {
                messages.Add("Стовпець 'Angle' не знайдено в таблиці.");
                return;
            }

            string cmdCol = FindColumn("Command", "command", "CMD", "cmd");

            var badRows = new List<string>();
            for (int i = 0; i < count; i++)
            {
                double angle = GetCellDouble(_grid.Rows[i], angleCol);

                bool isLand = cmdCol != null && CommandEquals(GetCellString(_grid.Rows[i], cmdCol), "LAND");
                double min = isLand ? LandAngleMin : AngleMin;
                double max = isLand ? LandAngleMax : AngleMax;

                if (angle < min || angle > max)
                {
                    badRows.Add($"точка {i + 1}={angle:0.##}° (дозволено [{min:0.#}..{max:0.#}])");
                    _rowsToHighlight.Add(i);
                }
            }

            if (badRows.Count > 0)
            {
                messages.Add($"Кут поза діапазоном: {string.Join(", ", badRows)}.");
            }
        }

        /// <summary>
        /// Rule 6: no point along the cruise portion of the route may be closer than
        /// 30 m to the terrain. The takeoff climb-out (first waypoint) and the landing
        /// descent (last waypoint) are excluded, since they are naturally near the ground.
        ///
        /// Runs on a background task so it never blocks Mission Planner: the route
        /// geometry is snapshotted here on the UI thread, the SRTM lookups happen
        /// off-thread, and the result is merged back via <see cref="RenderResults"/>.
        /// A geometry change cancels any in-flight scan and starts a new one; unchanged
        /// geometry is skipped.
        /// </summary>
        private void MaybeStartTerrainScan(int count)
        {
            string latCol = FindColumn("Lat", "lat", "LAT", "Latitude", "latitude");
            string lngCol = FindColumn("Long", "Lng", "lng", "long", "LON", "Lon", "Longitude", "longitude");
            string altCol = FindColumn("Alt", "alt", "ALT", "Altitude", "altitude", "Z", "z");

            if (latCol == null || lngCol == null || altCol == null)
            {
                SetTerrainResult("__nocols__",
                    new List<string> { "Просвіт над рельєфом: стовпці Lat/Long/Alt не знайдено в таблиці." },
                    new HashSet<int>());
                return;
            }

            // Cruise portion only: skip the takeoff waypoint (index 0) and the landing
            // waypoint (last), plus any command waypoints without a real position.
            var cruise = new List<int>();
            if (count >= 3)
            {
                for (int i = 1; i <= count - 2; i++)
                {
                    double lat = GetCellDouble(_grid.Rows[i], latCol);
                    double lng = GetCellDouble(_grid.Rows[i], lngCol);
                    if (HasPosition(lat, lng)) cruise.Add(i);
                }
            }

            // Snapshot the legs (UI-thread grid reads) plus a signature of the geometry,
            // so we can skip rescanning when nothing relevant changed.
            var legs = new List<TerrainLeg>();
            var sig = new StringBuilder();
            sig.Append(count).Append('|');
            for (int c = 0; c < cruise.Count - 1; c++)
            {
                int ia = cruise[c];
                int ib = cruise[c + 1];
                var leg = new TerrainLeg
                {
                    ARow = ia,
                    BRow = ib,
                    LatA = GetCellDouble(_grid.Rows[ia], latCol),
                    LngA = GetCellDouble(_grid.Rows[ia], lngCol),
                    AltA = GetCellDouble(_grid.Rows[ia], altCol),
                    LatB = GetCellDouble(_grid.Rows[ib], latCol),
                    LngB = GetCellDouble(_grid.Rows[ib], lngCol),
                    AltB = GetCellDouble(_grid.Rows[ib], altCol),
                };
                legs.Add(leg);
                sig.Append(leg.LatA).Append(',').Append(leg.LngA).Append(',').Append(leg.AltA).Append(';');
                sig.Append(leg.LatB).Append(',').Append(leg.LngB).Append(',').Append(leg.AltB).Append(';');
            }

            string signature = sig.ToString();
            if (signature == _terrainSig) return; // geometry unchanged - keep current result

            _terrainSig = signature;
            CancelTerrainScan();

            // Clear the stale result and show a "scanning" state until the new scan ends.
            _terrainMessages = new List<string>();
            _terrainRows = new HashSet<int>();

            if (legs.Count == 0)
            {
                _terrainScanning = false;
                RenderResults();
                return;
            }

            _terrainCts = new CancellationTokenSource();
            _terrainScanning = true;
            RenderResults();

            RunTerrainScan(legs, signature, _terrainCts.Token);
        }

        /// <summary>Applies a terrain result computed without a background scan (e.g. an error state).</summary>
        private void SetTerrainResult(string signature, List<string> messages, HashSet<int> rows)
        {
            if (signature == _terrainSig) return;
            _terrainSig = signature;
            CancelTerrainScan();
            _terrainMessages = messages;
            _terrainRows = rows;
            _terrainScanning = false;
            RenderResults();
        }

        private async void RunTerrainScan(List<TerrainLeg> legs, string signature, CancellationToken token)
        {
            TerrainScanResult result;
            try
            {
                result = await Task.Run(() => ComputeTerrain(legs, token), token);
            }
            catch (OperationCanceledException)
            {
                return; // superseded by a newer scan
            }
            catch (Exception ex)
            {
                _log($"RouteValidatorFeature: terrain scan error - {ex.Message}");
                return;
            }

            OnUi(() =>
            {
                // Ignore a result that a newer scan has already superseded.
                if (token.IsCancellationRequested || signature != _terrainSig) return;
                _terrainMessages = result.Messages;
                _terrainRows = result.Rows;
                _terrainScanning = false;
                RenderResults();
            });
        }

        /// <summary>
        /// Pure terrain computation - runs on a background thread and must NOT touch the
        /// grid. Samples each leg ~every 30 m and reports legs whose clearance drops
        /// below the minimum.
        /// </summary>
        private TerrainScanResult ComputeTerrain(List<TerrainLeg> legs, CancellationToken token)
        {
            double worstClearance = double.MaxValue;
            int worstA = -1, worstB = -1;
            int noDataSamples = 0;
            var badLegs = new List<string>();
            var rows = new HashSet<int>();

            foreach (var leg in legs)
            {
                token.ThrowIfCancellationRequested();

                double legDist = HaversineMeters(leg.LatA, leg.LngA, leg.LatB, leg.LngB);
                int samples = Math.Max(1, (int)Math.Ceiling(legDist / TerrainSampleSpacingM));

                double legMinClr = double.MaxValue;
                for (int k = 0; k <= samples; k++)
                {
                    token.ThrowIfCancellationRequested();

                    double t = (double)k / samples;
                    double lat = leg.LatA + (leg.LatB - leg.LatA) * t;
                    double lng = leg.LngA + (leg.LngB - leg.LngA) * t;
                    double plannedAlt = leg.AltA + (leg.AltB - leg.AltA) * t;

                    if (!TryGetGroundAltitude(lat, lng, out double ground))
                    {
                        noDataSamples++;
                        continue;
                    }

                    double clearance = plannedAlt - ground;
                    if (clearance < legMinClr) legMinClr = clearance;
                }

                if (legMinClr < MinTerrainClearance)
                {
                    badLegs.Add($"між точками {leg.ARow + 1} і {leg.BRow + 1} ({legMinClr:0.#} м)");
                    rows.Add(leg.ARow);
                    rows.Add(leg.BRow);
                    if (legMinClr < worstClearance) { worstClearance = legMinClr; worstA = leg.ARow; worstB = leg.BRow; }
                }
            }

            var messages = new List<string>();
            if (badLegs.Count > 0)
            {
                messages.Add(
                    $"Просвіт над рельєфом < {MinTerrainClearance:0.#} м на ділянках: {string.Join(", ", badLegs)}. " +
                    $"Мінімум {worstClearance:0.#} м між точками {worstA + 1} і {worstB + 1}.");
            }
            if (noDataSamples > 0)
            {
                messages.Add(
                    $"Просвіт над рельєфом: дані рельєфу відсутні для {noDataSamples} точок — перевірку виконано частково " +
                    "(можливо, дані SRTM ще завантажуються; повторіть перевірку за кілька секунд).");
            }

            return new TerrainScanResult { Messages = messages, Rows = rows };
        }

        private void CancelTerrainScan()
        {
            try { _terrainCts?.Cancel(); } catch { }
            _terrainCts = null;
            _terrainScanning = false;
        }

        /// <summary>Runs an action on the UI thread that owns the grid/button.</summary>
        private void OnUi(Action action)
        {
            try
            {
                Control c = _validateButton;
                if (c != null && c.IsHandleCreated && c.InvokeRequired)
                    c.BeginInvoke(action);
                else
                    action();
            }
            catch { }
        }

        private sealed class TerrainLeg
        {
            public int ARow, BRow;
            public double LatA, LngA, AltA;
            public double LatB, LngB, AltB;
        }

        private sealed class TerrainScanResult
        {
            public List<string> Messages = new List<string>();
            public HashSet<int> Rows = new HashSet<int>();
        }

        private static bool HasPosition(double lat, double lng)
        {
            if (Math.Abs(lat) < 0.0000001 && Math.Abs(lng) < 0.0000001) return false;
            return lat >= -90 && lat <= 90 && lng >= -180 && lng <= 180;
        }

        /// <summary>
        /// Terrain height (AMSL) at a point from Mission Planner's SRTM data. Returns
        /// false when data is unavailable (e.g. the tile is still downloading) so the
        /// caller reports a partial check rather than a false pass.
        /// </summary>
        private bool TryGetGroundAltitude(double lat, double lng, out double groundAlt)
        {
            groundAlt = 0;
            try
            {
                var resp = MissionPlanner.Utilities.srtm.getAltitude(lat, lng);
                // Compare by enum name so we don't depend on where the altsource type is
                // declared; "Invalid" means SRTM has no data for this point yet.
                if (resp.altsource.ToString().Equals("Invalid", StringComparison.OrdinalIgnoreCase))
                    return false;
                groundAlt = resp.alt;
                return true;
            }
            catch (Exception ex)
            {
                _log($"RouteValidatorFeature: terrain lookup failed - {ex.Message}");
                return false;
            }
        }

        private static double HaversineMeters(double lat1, double lng1, double lat2, double lng2)
        {
            const double R = 6378137.0; // Earth radius (m)
            double dLat = (lat2 - lat1) * Math.PI / 180.0;
            double dLng = (lng2 - lng1) * Math.PI / 180.0;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                       Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        /// <summary>
        /// Exact (case-insensitive) command-name match, so e.g. "VTOL_TAKEOFF" does not
        /// satisfy a "TAKEOFF" requirement.
        /// </summary>
        private static bool CommandEquals(string command, string expected)
        {
            if (string.IsNullOrEmpty(command)) return false;
            return command.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Cell highlighting

        private void ApplyHighlights(HashSet<int> rowsToHighlight)
        {
            ClearHighlights();

            foreach (int rowIndex in rowsToHighlight)
            {
                try
                {
                    if (rowIndex < 0 || rowIndex >= _grid.RowCount) continue;

                    var row = _grid.Rows[rowIndex];
                    row.DefaultCellStyle.BackColor = BadCellColor;
                    row.DefaultCellStyle.ForeColor = BadCellForeColor;
                    _highlightedRows.Add(row);
                }
                catch { }
            }
        }

        private void ClearHighlights()
        {
            foreach (var row in _highlightedRows)
            {
                try
                {
                    // Reset to defaults so MP's own row styling takes over again.
                    row.DefaultCellStyle.BackColor = Color.Empty;
                    row.DefaultCellStyle.ForeColor = Color.Empty;
                }
                catch { }
            }
            _highlightedRows.Clear();
        }

        #endregion

        #region Helpers

        private static bool IsAbsoluteFrame(string frame)
        {
            if (string.IsNullOrEmpty(frame)) return false;
            string f = frame.Trim().ToLowerInvariant();
            // MP shows "Absolute" for the abs frame; numeric MAV_FRAME 0 is GLOBAL (absolute alt).
            return f.Contains("abs") || f.Contains("global") || f == "0";
        }

        /// <summary>
        /// Smallest absolute angular difference between two bearings, accounting for wrap.
        /// </summary>
        private static double AngularDiff(double a, double b)
        {
            double d = Math.Abs(a - b) % 360.0;
            return d > 180.0 ? 360.0 - d : d;
        }

        /// <summary>
        /// Number of "real" waypoint rows, skipping any trailing empty/new row that
        /// the grid keeps for user input.
        /// </summary>
        private int GetEffectiveRowCount()
        {
            if (_grid == null) return 0;

            string cmdCol = FindColumn("Command", "command", "CMD", "cmd");

            int count = _grid.RowCount;
            for (int i = _grid.RowCount - 1; i >= 0; i--)
            {
                var row = _grid.Rows[i];
                if (row.IsNewRow)
                {
                    count = i;
                    continue;
                }

                if (cmdCol != null)
                {
                    string cmd = GetCellString(row, cmdCol);
                    if (string.IsNullOrWhiteSpace(cmd))
                    {
                        count = i;
                        continue;
                    }
                }

                break;
            }

            return count;
        }

        /// <summary>
        /// Cheap fingerprint of the cells the validator cares about, used to detect
        /// mission-file loads and other external changes.
        /// </summary>
        private string ComputeSignature()
        {
            if (_grid == null) return "";

            string azCol = FindColumn("AZ", "az", "Azimuth", "azimuth");
            string frameCol = FindColumn("Frame", "frame", "FRAME");
            string angleCol = FindColumn("Angle", "angle", "ANGLE");
            string altCol = FindColumn("Alt", "alt", "ALT", "Altitude", "altitude", "Z", "z");
            string latCol = FindColumn("Lat", "lat", "LAT", "Latitude", "latitude");
            string lngCol = FindColumn("Long", "Lng", "lng", "long", "LON", "Lon", "Longitude", "longitude");

            var sb = new StringBuilder();
            sb.Append(_grid.RowCount).Append('|');
            // Home altitude lives outside the grid, so include it: editing it raises no
            // grid event but must still re-trigger the 2nd-waypoint-altitude check.
            if (TryGetHomeAltitude(out double homeAlt)) sb.Append(homeAlt);
            sb.Append('|');

            for (int i = 0; i < _grid.RowCount; i++)
            {
                var row = _grid.Rows[i];
                if (azCol != null) sb.Append(GetCellString(row, azCol));
                sb.Append(',');
                if (frameCol != null) sb.Append(GetCellString(row, frameCol));
                sb.Append(',');
                if (angleCol != null) sb.Append(GetCellString(row, angleCol));
                sb.Append(',');
                if (altCol != null) sb.Append(GetCellString(row, altCol));
                sb.Append(',');
                if (latCol != null) sb.Append(GetCellString(row, latCol));
                sb.Append(',');
                if (lngCol != null) sb.Append(GetCellString(row, lngCol));
                sb.Append(';');
            }

            return sb.ToString();
        }

        /// <summary>
        /// Reads the Flight Planner's home altitude from its "Home Alt" text box
        /// (TXT_homealt), which sits on the same form as the waypoint grid.
        /// </summary>
        private bool TryGetHomeAltitude(out double homeAlt)
        {
            homeAlt = 0;

            Control box = null;
            try
            {
                Control root = _grid?.TopLevelControl ?? _grid?.FindForm();
                box = FindControlByName(root, "TXT_homealt");
                if (box == null)
                {
                    // Fallback: the box may live on a different open form than the grid.
                    // Snapshot the collection first — enumerating Application.OpenForms
                    // directly can throw if a form opens/closes mid-iteration.
                    var forms = new List<Form>();
                    foreach (Form form in Application.OpenForms) forms.Add(form);
                    foreach (Form form in forms)
                    {
                        box = FindControlByName(form, "TXT_homealt");
                        if (box != null) break;
                    }
                }
            }
            catch { box = null; }

            if (box == null) return false;

            string text = box.Text;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string strVal = text.Trim().Replace(',', '.');
            return double.TryParse(strVal, NumberStyles.Any,
                CultureInfo.InvariantCulture, out homeAlt);
        }

        private static Control FindControlByName(Control parent, string name)
        {
            if (parent == null) return null;

            try
            {
                if (string.Equals(parent.Name, name, StringComparison.Ordinal))
                    return parent;

                foreach (Control child in parent.Controls)
                {
                    var found = FindControlByName(child, name);
                    if (found != null) return found;
                }
            }
            catch { }

            return null;
        }

        private string FindColumn(params string[] names)
        {
            foreach (var name in names)
            {
                if (_grid.Columns.Contains(name))
                    return name;
            }
            return null;
        }

        private string GetCellString(DataGridViewRow row, string colName)
        {
            try
            {
                var val = row.Cells[colName].Value;
                return val?.ToString() ?? "";
            }
            catch { return ""; }
        }

        private double GetCellDouble(DataGridViewRow row, string colName)
        {
            try
            {
                var val = row.Cells[colName].Value;
                if (val == null) return 0;

                if (val is double d) return d;
                if (val is float f) return f;
                if (val is decimal dec) return (double)dec;
                if (val is int i) return i;

                string strVal = val.ToString().Replace(',', '.');
                if (double.TryParse(strVal, NumberStyles.Any,
                    CultureInfo.InvariantCulture, out double result))
                {
                    return result;
                }
            }
            catch { }
            return 0;
        }

        #endregion
    }
}
