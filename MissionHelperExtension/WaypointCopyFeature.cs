using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MissionPlanner.Plugin;

namespace MissionHelperExtension
{
    /// <summary>
    /// Feature: Copy/Paste waypoints functionality
    /// </summary>
    public class WaypointCopyFeature
    {
        private readonly DataGridView _grid;
        private readonly Action<string> _log;
        private readonly PluginHost _host;

        private Panel _controlPanel;
        private NumericUpDown _numPointsInput;
        private Button _copyButton;
        private Button _pasteButton;
        private Label _statusLabel;

        private List<WaypointData> _copiedWaypoints = new List<WaypointData>();

        /// <summary>
        /// The bottom-docked panel holding the copy/paste controls, so other features
        /// (e.g. the route validator) can share the same line. Null until UI is created.
        /// </summary>
        public Panel ControlPanel => _controlPanel;

        public WaypointCopyFeature(DataGridView grid, Action<string> log, PluginHost host)
        {
            _grid = grid;
            _log = log;
            _host = host;
        }

        public void Initialize()
        {
            _log("WaypointCopyFeature: Initializing...");

            CreateUI();

            _log("WaypointCopyFeature: Initialized");
        }

        public void Cleanup()
        {
            if (_controlPanel != null)
            {
                _controlPanel.Parent?.Controls.Remove(_controlPanel);
                _controlPanel.Dispose();
            }
        }

        private void CreateUI()
        {
            // Find parent container to add our controls
            Control parent = _grid.Parent;
            if (parent == null)
            {
                _log("WaypointCopyFeature: Grid has no parent, cannot create UI");
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
                Text = "Копіювати перші",
                AutoSize = true,
                Location = new Point(5, 7),
                ForeColor = Color.White
            };

            _numPointsInput = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 100,
                Value = 5,
                Width = 52,
                Location = new Point(125, 4),
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White
            };

            var label2 = new Label
            {
                Text = "точок",
                AutoSize = true,
                Location = new Point(183, 7),
                ForeColor = Color.White
            };

            _copyButton = new Button
            {
                Text = "Копіювати",
                Width = 96,
                Location = new Point(232, 3),
                BackColor = Color.FromArgb(0, 128, 0),  // Green
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat
            };
            _copyButton.FlatAppearance.BorderColor = Color.DarkGreen;
            _copyButton.Click += OnCopyClick;

            _pasteButton = new Button
            {
                Text = "Вставити",
                Width = 96,
                Location = new Point(334, 3),
                Enabled = false,
                BackColor = Color.FromArgb(0, 128, 0),  // Green
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat
            };
            _pasteButton.FlatAppearance.BorderColor = Color.DarkGreen;
            _pasteButton.Click += OnPasteClick;

            _statusLabel = new Label
            {
                Text = "",
                AutoSize = true,
                Location = new Point(438, 7),
                ForeColor = Color.LightGreen
            };

            // Lay the controls out left-to-right using each label's measured width, so
            // the (wider) Ukrainian captions never overlap regardless of font/DPI.
            const int gap = 8;
            int x = label.Location.X + label.PreferredWidth + gap;
            _numPointsInput.Location = new Point(x, 4);
            x += _numPointsInput.Width + gap;
            label2.Location = new Point(x, 7);
            x += label2.PreferredWidth + gap;
            _copyButton.Location = new Point(x, 3);
            x += _copyButton.Width + gap;
            _pasteButton.Location = new Point(x, 3);
            x += _pasteButton.Width + gap;
            _statusLabel.Location = new Point(x, 7);

            _controlPanel.Controls.Add(label);
            _controlPanel.Controls.Add(_numPointsInput);
            _controlPanel.Controls.Add(label2);
            _controlPanel.Controls.Add(_copyButton);
            _controlPanel.Controls.Add(_pasteButton);
            _controlPanel.Controls.Add(_statusLabel);

            parent.SuspendLayout();
            try
            {
                parent.Controls.Add(_controlPanel);
                // SendToBack so this bottom-docked panel is laid out before the Fill grid,
                // leaving the grid to occupy the remaining space above it instead of overlapping.
                _controlPanel.SendToBack();

                // Fallback for parents where the grid isn't Dock=Fill: shrink the grid
                // by the panel's height so the panel sits below the last row, not on top of it.
                if (_grid.Dock != DockStyle.Fill && _grid.Dock != DockStyle.Bottom)
                {
                    _grid.Height = Math.Max(0, _grid.Height - _controlPanel.Height);
                }
            }
            finally
            {
                parent.ResumeLayout(true);
            }

            _log("WaypointCopyFeature: UI created");
        }

        private void OnCopyClick(object sender, EventArgs e)
        {
            try
            {
                int numPoints = (int)_numPointsInput.Value;
                _copiedWaypoints.Clear();

                int pointsToCopy = Math.Min(numPoints, _grid.RowCount);

                _log($"WaypointCopyFeature: Copying {pointsToCopy} waypoints...");

                // Log grid columns for debugging
                _log("WaypointCopyFeature: Grid columns:");
                for (int c = 0; c < _grid.ColumnCount; c++)
                {
                    var col = _grid.Columns[c];
                    // Also log first row value for each column
                    object firstVal = _grid.RowCount > 0 ? _grid.Rows[0].Cells[c].Value : null;
                    _log($"  [{c}] Name='{col.Name}', Header='{col.HeaderText}', FirstVal='{firstVal}'");
                }

                for (int i = 0; i < pointsToCopy; i++)
                {
                    var row = _grid.Rows[i];
                    var wp = new WaypointData();

                    // Extract waypoint data using column names (try multiple variations)
                    wp.Command = GetCellStringMulti(row, "Command", "command", "CMD", "cmd");
                    wp.Param1 = GetCellDoubleMulti(row, "Param1", "param1", "P1", "p1");
                    wp.Param2 = GetCellDoubleMulti(row, "Param2", "param2", "P2", "p2");
                    wp.Param3 = GetCellDoubleMulti(row, "Param3", "param3", "P3", "p3");
                    wp.Param4 = GetCellDoubleMulti(row, "Param4", "param4", "P4", "p4");
                    wp.Lat = GetCellDoubleMulti(row, "Lat", "lat", "LAT", "Latitude", "latitude");
                    wp.Lng = GetCellDoubleMulti(row, "Long", "Lng", "lng", "long", "LON", "Lon", "Longitude", "longitude");
                    wp.Alt = GetCellDoubleMulti(row, "Alt", "alt", "ALT", "Altitude", "altitude", "Z", "z");
                    wp.Frame = GetCellStringMulti(row, "Frame", "frame", "FRAME");

                    // Log first waypoint for debugging
                    if (i == 0)
                    {
                        _log($"  WP[0]: Cmd={wp.Command}, P1={wp.Param1}, P2={wp.Param2}, P3={wp.Param3}, P4={wp.Param4}");
                        _log($"         Lat={wp.Lat}, Lng={wp.Lng}, Alt={wp.Alt}, Frame={wp.Frame}");
                    }

                    _copiedWaypoints.Add(wp);
                }

                _pasteButton.Enabled = _copiedWaypoints.Count > 0;
                _statusLabel.Text = $"Скопійовано {_copiedWaypoints.Count} точок";
                _statusLabel.ForeColor = Color.DarkGreen;

                _log($"WaypointCopyFeature: Copied {_copiedWaypoints.Count} waypoints");
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Помилка: {ex.Message}";
                _statusLabel.ForeColor = Color.Red;
                _log($"WaypointCopyFeature: Copy error - {ex.Message}\n{ex.StackTrace}");
            }
        }

        private string GetCellStringMulti(DataGridViewRow row, params string[] colNames)
        {
            foreach (var colName in colNames)
            {
                if (_grid.Columns.Contains(colName))
                {
                    var val = row.Cells[colName].Value;
                    return val?.ToString() ?? "";
                }
            }
            return "";
        }

        private double GetCellDoubleMulti(DataGridViewRow row, params string[] colNames)
        {
            foreach (var colName in colNames)
            {
                if (_grid.Columns.Contains(colName))
                {
                    var val = row.Cells[colName].Value;
                    if (val == null) continue;

                    if (val is double d) return d;
                    if (val is float f) return f;
                    if (val is decimal dec) return (double)dec;
                    if (val is int i) return i;

                    string strVal = val.ToString().Replace(',', '.');
                    if (double.TryParse(strVal,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double result))
                    {
                        return result;
                    }
                }
            }
            return 0;
        }

        private void OnPasteClick(object sender, EventArgs e)
        {
            try
            {
                if (_copiedWaypoints.Count == 0)
                {
                    _statusLabel.Text = "Немає точок для вставки";
                    _statusLabel.ForeColor = Color.Orange;
                    return;
                }

                _log($"WaypointCopyFeature: Pasting {_copiedWaypoints.Count} waypoints using Host.InsertWP...");

                // Insert waypoints using MissionPlanner's API (in reverse order so they end up at the beginning)
                for (int i = _copiedWaypoints.Count - 1; i >= 0; i--)
                {
                    var wp = _copiedWaypoints[i];

                    // Parse command string to MAV_CMD enum
                    MAVLink.MAV_CMD cmd = ParseCommand(wp.Command);

                    _log($"  Inserting WP at index 0: Cmd={cmd}, P1={wp.Param1}, P2={wp.Param2}, P3={wp.Param3}, P4={wp.Param4}, Lat={wp.Lat}, Lng={wp.Lng}, Alt={wp.Alt}");

                    // Use Host.InsertWP to properly insert waypoint
                    // Note: InsertWP expects x=Lng, y=Lat (not Lat, Lng)
                    _host.InsertWP(0, cmd, wp.Param1, wp.Param2, wp.Param3, wp.Param4, wp.Lng, wp.Lat, wp.Alt);
                }

                _statusLabel.Text = $"Вставлено {_copiedWaypoints.Count} точок";
                _statusLabel.ForeColor = Color.DarkGreen;

                _log($"WaypointCopyFeature: Pasted {_copiedWaypoints.Count} waypoints successfully");
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Помилка: {ex.Message}";
                _statusLabel.ForeColor = Color.Red;
                _log($"WaypointCopyFeature: Paste error - {ex.Message}\n{ex.StackTrace}");
            }
        }

        private MAVLink.MAV_CMD ParseCommand(string commandStr)
        {
            if (string.IsNullOrEmpty(commandStr))
                return MAVLink.MAV_CMD.WAYPOINT;

            // Try to parse as enum
            if (Enum.TryParse<MAVLink.MAV_CMD>(commandStr, true, out var cmd))
                return cmd;

            // Try common command name mappings
            string upper = commandStr.ToUpperInvariant();
            if (upper.Contains("WAYPOINT") || upper == "WP")
                return MAVLink.MAV_CMD.WAYPOINT;
            if (upper.Contains("TAKEOFF"))
                return MAVLink.MAV_CMD.TAKEOFF;
            if (upper.Contains("LAND"))
                return MAVLink.MAV_CMD.LAND;
            if (upper.Contains("RTL") || upper.Contains("RETURN"))
                return MAVLink.MAV_CMD.RETURN_TO_LAUNCH;
            if (upper.Contains("LOITER_UNLIM"))
                return MAVLink.MAV_CMD.LOITER_UNLIM;
            if (upper.Contains("LOITER_TIME"))
                return MAVLink.MAV_CMD.LOITER_TIME;
            if (upper.Contains("LOITER_TURNS"))
                return MAVLink.MAV_CMD.LOITER_TURNS;

            _log($"WaypointCopyFeature: Unknown command '{commandStr}', defaulting to WAYPOINT");
            return MAVLink.MAV_CMD.WAYPOINT;
        }

        /// <summary>
        /// Stores waypoint data for copy/paste
        /// </summary>
        private class WaypointData
        {
            public string Command { get; set; }
            public double Param1 { get; set; }
            public double Param2 { get; set; }
            public double Param3 { get; set; }
            public double Param4 { get; set; }
            public double Lat { get; set; }
            public double Lng { get; set; }
            public double Alt { get; set; }
            public string Frame { get; set; }
        }
    }
}
