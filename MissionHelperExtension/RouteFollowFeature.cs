using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using GMap.NET;
using MissionPlanner.Plugin;

namespace MissionHelperExtension
{
    /// <summary>
    /// Feature: a "route preview" slider. Dragging it from left to right pans the Flight
    /// Planner map along the whole route (home -> last waypoint), centring the map on the
    /// point at that fraction of the total route length. The position is interpolated
    /// along each leg, so the map glides smoothly instead of jumping between waypoints.
    ///
    /// The slider only moves the map when the slider itself changes: zoom is never
    /// touched, and the map can still be panned/zoomed manually at any time.
    /// </summary>
    public class RouteFollowFeature
    {
        // Slider resolution. A high value keeps each step small (a few metres on a
        // typical route), so dragging feels continuous.
        private const int SliderMax = 2000;

        private readonly DataGridView _grid;
        private readonly Action<string> _log;
        private readonly PluginHost _host;
        private readonly Panel _belowPanel;

        private Panel _panel;
        private TrackBar _slider;
        private Label _percentLabel;

        private Control _map;
        private PropertyInfo _mapPositionProp;

        /// <param name="belowPanel">
        /// Optional bottom-docked panel (e.g. the copy feature's control line) that the
        /// slider should sit directly above. If null, the slider docks at the very bottom.
        /// </param>
        public RouteFollowFeature(DataGridView grid, Action<string> log, PluginHost host, Panel belowPanel = null)
        {
            _grid = grid;
            _log = log;
            _host = host;
            _belowPanel = belowPanel;
        }

        public void Initialize()
        {
            _log("RouteFollowFeature: Initializing...");

            CreateUI();

            _log("RouteFollowFeature: Initialized");
        }

        public void Cleanup()
        {
            if (_slider != null)
                _slider.ValueChanged -= OnSliderChanged;

            if (_panel != null)
            {
                _panel.Parent?.Controls.Remove(_panel);
                _panel.Dispose();
            }
        }

        private void CreateUI()
        {
            Control parent = _grid.Parent;
            if (parent == null)
            {
                _log("RouteFollowFeature: Grid has no parent, cannot create UI");
                return;
            }

            _panel = new Panel
            {
                Height = 30,
                Dock = DockStyle.Bottom,
                Padding = new Padding(5, 3, 5, 3),
                BackColor = Color.FromArgb(38, 38, 38)  // Dark background to match MP
            };

            var label = new Label
            {
                Text = "Огляд маршруту",
                AutoSize = true,
                Dock = DockStyle.Left,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(0, 4, 0, 0),
                ForeColor = Color.White
            };

            _percentLabel = new Label
            {
                Text = "0%",
                AutoSize = false,
                Width = 44,
                Dock = DockStyle.Right,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.White
            };

            // The slider fills the remaining width so it is as long as the grid allows.
            _slider = new TrackBar
            {
                Minimum = 0,
                Maximum = SliderMax,
                SmallChange = 1,
                LargeChange = SliderMax / 50,
                TickStyle = TickStyle.None,
                AutoSize = false,
                Height = 24,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(38, 38, 38)
            };
            _slider.ValueChanged += OnSliderChanged;

            // Fill control first, then the edge-docked ones, so docking lays out correctly.
            _panel.Controls.Add(_slider);
            _panel.Controls.Add(_percentLabel);
            _panel.Controls.Add(label);

            parent.SuspendLayout();
            try
            {
                parent.Controls.Add(_panel);

                // Bottom-docked controls are laid out from the end of the Controls
                // collection, so taking the below-panel's index puts our panel right
                // above it. Without one, SendToBack docks us at the very bottom.
                if (_belowPanel != null && _belowPanel.Parent == parent)
                    parent.Controls.SetChildIndex(_panel, parent.Controls.GetChildIndex(_belowPanel));
                else
                    _panel.SendToBack();

                if (_grid.Dock != DockStyle.Fill && _grid.Dock != DockStyle.Bottom)
                {
                    _grid.Height = Math.Max(0, _grid.Height - _panel.Height);
                }
            }
            finally
            {
                parent.ResumeLayout(true);
            }

            _log("RouteFollowFeature: UI created");
        }

        private void OnSliderChanged(object sender, EventArgs e)
        {
            double t = (double)_slider.Value / SliderMax;
            _percentLabel.Text = $"{t * 100:0}%";

            try
            {
                var track = BuildTrack();
                if (track.Count == 0) return;

                PointLatLng p = PointAlongTrack(track, t);
                SetMapCenter(p);
            }
            catch (Exception ex)
            {
                _log($"RouteFollowFeature: error moving map - {ex.Message}");
            }
        }

        #region Track geometry

        /// <summary>
        /// The route as flown: home first (when known), then every waypoint that has a
        /// real position, in grid order. Rows without coordinates are skipped.
        /// </summary>
        private List<PointLatLng> BuildTrack()
        {
            var pts = new List<PointLatLng>();

            if (TryReadNamedDouble("TXT_homelat", out double hLat) &&
                TryReadNamedDouble("TXT_homelng", out double hLng) &&
                HasPosition(hLat, hLng))
            {
                pts.Add(new PointLatLng(hLat, hLng));
            }

            string latCol = FindColumn("Lat", "lat", "LAT", "Latitude", "latitude");
            string lngCol = FindColumn("Long", "Lng", "lng", "long", "LON", "Lon", "Longitude", "longitude");
            if (latCol == null || lngCol == null) return pts;

            for (int i = 0; i < _grid.RowCount; i++)
            {
                var row = _grid.Rows[i];
                if (row.IsNewRow) continue;

                double lat = GetCellDouble(row, latCol);
                double lng = GetCellDouble(row, lngCol);
                if (HasPosition(lat, lng)) pts.Add(new PointLatLng(lat, lng));
            }

            return pts;
        }

        /// <summary>
        /// The point at fraction <paramref name="t"/> (0..1) of the total track length,
        /// linearly interpolated within the leg it falls on.
        /// </summary>
        private static PointLatLng PointAlongTrack(List<PointLatLng> track, double t)
        {
            if (track.Count == 1) return track[0];

            var legLen = new double[track.Count - 1];
            double total = 0;
            for (int i = 0; i < legLen.Length; i++)
            {
                legLen[i] = HaversineMeters(track[i].Lat, track[i].Lng, track[i + 1].Lat, track[i + 1].Lng);
                total += legLen[i];
            }
            if (total <= 0) return track[0];

            double target = Math.Max(0, Math.Min(1, t)) * total;
            for (int i = 0; i < legLen.Length; i++)
            {
                if (target <= legLen[i] || i == legLen.Length - 1)
                {
                    double f = legLen[i] > 0 ? Math.Min(1, target / legLen[i]) : 0;
                    var a = track[i];
                    var b = track[i + 1];
                    return new PointLatLng(a.Lat + (b.Lat - a.Lat) * f, a.Lng + (b.Lng - a.Lng) * f);
                }
                target -= legLen[i];
            }

            return track[track.Count - 1];
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

        private static bool HasPosition(double lat, double lng)
        {
            if (Math.Abs(lat) < 0.0000001 && Math.Abs(lng) < 0.0000001) return false;
            return lat >= -90 && lat <= 90 && lng >= -180 && lng <= 180;
        }

        #endregion

        #region Map access

        /// <summary>
        /// Centres the Flight Planner map on a point without changing the zoom.
        /// The map (a GMapControl) is accessed via reflection so the plugin does not need
        /// a compile-time reference to GMap.NET.WindowsForms.
        /// </summary>
        private void SetMapCenter(PointLatLng p)
        {
            if (!EnsureMap()) return;
            _mapPositionProp.SetValue(_map, p, null);
        }

        private bool EnsureMap()
        {
            if (_map != null && !_map.IsDisposed && _mapPositionProp != null) return true;

            _map = null;
            _mapPositionProp = null;

            // Preferred: the Flight Planner map exposed by the plugin host.
            try
            {
                var prop = _host?.GetType().GetProperty("FPGMapControl");
                _map = prop?.GetValue(_host, null) as Control;
            }
            catch { _map = null; }

            // Fallback: look for a GMapControl on the grid's form.
            if (_map == null)
                _map = FindMapControl(_grid?.TopLevelControl ?? _grid?.FindForm());

            if (_map == null)
            {
                _log("RouteFollowFeature: Flight Planner map not found");
                return false;
            }

            _mapPositionProp = _map.GetType().GetProperty("Position", typeof(PointLatLng));
            if (_mapPositionProp == null || !_mapPositionProp.CanWrite)
            {
                _log($"RouteFollowFeature: map type '{_map.GetType().FullName}' has no writable Position");
                _map = null;
                _mapPositionProp = null;
                return false;
            }

            _log($"RouteFollowFeature: using map '{_map.Name}' ({_map.GetType().FullName})");
            return true;
        }

        private static Control FindMapControl(Control parent)
        {
            if (parent == null) return null;

            try
            {
                for (Type t = parent.GetType(); t != null; t = t.BaseType)
                {
                    if (t.Name == "GMapControl") return parent;
                }

                foreach (Control child in parent.Controls)
                {
                    var found = FindMapControl(child);
                    if (found != null) return found;
                }
            }
            catch { }

            return null;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Reads a named text box (searched on the grid's form, then any open form) and
        /// parses its text as a double.
        /// </summary>
        private bool TryReadNamedDouble(string controlName, out double value)
        {
            value = 0;

            Control box = null;
            try
            {
                Control root = _grid?.TopLevelControl ?? _grid?.FindForm();
                box = FindControlByName(root, controlName);
                if (box == null)
                {
                    var forms = new List<Form>();
                    foreach (Form form in Application.OpenForms) forms.Add(form);
                    foreach (Form form in forms)
                    {
                        box = FindControlByName(form, controlName);
                        if (box != null) break;
                    }
                }
            }
            catch { box = null; }

            if (box == null || string.IsNullOrWhiteSpace(box.Text)) return false;

            string strVal = box.Text.Trim().Replace(',', '.');
            return double.TryParse(strVal, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
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
