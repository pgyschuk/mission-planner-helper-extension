using System;
using System.Collections.Generic;
using System.Windows.Forms;
using MissionPlanner.Plugin;

namespace MissionHelperExtension
{
    /// <summary>
    /// Main plugin class - combines all MissionHelper extension features
    /// </summary>
    public class MissionHelperExtensionPlugin : Plugin
    {
        private const string LOG_PREFIX = "MissionHelperExtension: ";

        // A self-written log file, so diagnostics are visible regardless of how Mission
        // Planner is launched (its normal log does not capture Console/Debug output).
        // On Windows this resolves to %TEMP%\MissionHelperExtension.log.
        private static readonly string LogFilePath =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MissionHelperExtension.log");

        public override string Name => "MissionHelper Extension";
        public override string Version => "1.0.0";
        public override string Author => "Custom Plugin";

        private DataGridView _wpGrid;
        private System.Windows.Forms.Timer _searchTimer;
        private int _timerTickCount;

        // Features
        private DistanceTotalFeature _distanceFeature;
        private WaypointCopyFeature _copyFeature;
        private RouteValidatorFeature _validatorFeature;

        private void Log(string message)
        {
            var fullMessage = LOG_PREFIX + message;
            Console.WriteLine(fullMessage);
            System.Diagnostics.Debug.WriteLine(fullMessage);
            try
            {
                System.IO.File.AppendAllText(
                    LogFilePath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + fullMessage + Environment.NewLine);
            }
            catch { }
        }

        public override bool Init()
        {
            Log("Init called");
            return true;
        }

        public override bool Loaded()
        {
            Log("Loaded called - starting search timer");

            try
            {
                _searchTimer = new System.Windows.Forms.Timer();
                _searchTimer.Interval = 2000;
                _searchTimer.Tick += OnTimerTick;
                _searchTimer.Start();
                Log("Timer started (2 second interval)");
            }
            catch (Exception ex)
            {
                Log($"ERROR starting timer: {ex.Message}");
            }

            return true;
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            _timerTickCount++;
            Log($"Timer tick #{_timerTickCount}");

            try
            {
                if (_wpGrid == null)
                {
                    Log("Searching for grid...");
                    _wpGrid = GlobalSearchForWaypointGrid();

                    if (_wpGrid != null)
                    {
                        Log($"SUCCESS! Grid found: Name='{_wpGrid.Name}', Columns={_wpGrid.Columns.Count}");

                        // Stop searching before we initialize: the grid is found, so even
                        // if a feature fails to init we must not loop back and re-run setup
                        // (which would duplicate columns/panels).
                        _searchTimer.Stop();
                        Log("Timer stopped - grid found");

                        InitializeFeatures();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"ERROR in timer tick: {ex.Message}");
                _wpGrid = null;
            }
        }

        private void InitializeFeatures()
        {
            Log("Initializing features...");

            // Each feature is initialized independently: a failure in one must not
            // prevent the others from loading.
            try
            {
                _distanceFeature = new DistanceTotalFeature(_wpGrid, Log);
                _distanceFeature.Initialize();
            }
            catch (Exception ex)
            {
                Log($"ERROR initializing DistanceTotalFeature: {ex.Message}\n{ex.StackTrace}");
            }

            try
            {
                _copyFeature = new WaypointCopyFeature(_wpGrid, Log, Host);
                _copyFeature.Initialize();
            }
            catch (Exception ex)
            {
                Log($"ERROR initializing WaypointCopyFeature: {ex.Message}\n{ex.StackTrace}");
            }

            try
            {
                // Share the copy feature's control line when it created one.
                Panel hostPanel = _copyFeature?.ControlPanel;
                _validatorFeature = new RouteValidatorFeature(_wpGrid, Log, hostPanel);
                _validatorFeature.Initialize();
            }
            catch (Exception ex)
            {
                Log($"ERROR initializing RouteValidatorFeature: {ex.Message}\n{ex.StackTrace}");
            }

            Log("All features initialized");
        }

        public override bool Loop()
        {
            return true;
        }

        public override bool Exit()
        {
            Log("Exit called");

            _searchTimer?.Stop();
            _searchTimer?.Dispose();

            _distanceFeature?.Cleanup();
            _copyFeature?.Cleanup();
            _validatorFeature?.Cleanup();

            return true;
        }

        #region Grid Discovery

        private DataGridView GlobalSearchForWaypointGrid()
        {
            var allGrids = new List<DataGridView>();

            try
            {
                foreach (Form form in Application.OpenForms)
                {
                    try
                    {
                        FindAllDataGridViews(form, allGrids);
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                if (Host.MainForm != null)
                {
                    FindAllDataGridViews(Host.MainForm, allGrids);
                }
            }
            catch { }

            Log($"Total DataGridViews found: {allGrids.Count}");

            foreach (var grid in allGrids)
            {
                try
                {
                    if (IsWaypointGrid(grid))
                        return grid;
                }
                catch { }
            }

            return null;
        }

        private void FindAllDataGridViews(Control parent, List<DataGridView> results)
        {
            if (parent == null) return;

            try
            {
                if (parent is DataGridView grid && !results.Contains(grid))
                {
                    results.Add(grid);
                }

                foreach (Control child in parent.Controls)
                {
                    try { FindAllDataGridViews(child, results); } catch { }
                }

                if (parent is TabControl tabControl)
                {
                    foreach (TabPage tab in tabControl.TabPages)
                    {
                        try { FindAllDataGridViews(tab, results); } catch { }
                    }
                }
            }
            catch { }
        }

        private bool IsWaypointGrid(DataGridView grid)
        {
            bool hasCommand = grid.Columns.Contains("Command") || grid.Columns.Contains("command");
            bool hasLat = grid.Columns.Contains("Lat") || grid.Columns.Contains("lat");
            bool hasLng = grid.Columns.Contains("Long") || grid.Columns.Contains("Lng");

            string nameLower = (grid.Name ?? "").ToLower();
            bool hasWpName = nameLower.Contains("command") || nameLower.Contains("waypoint") ||
                             nameLower.Contains("mission") || nameLower.Contains("dgv");

            bool isMatch = grid.Columns.Count > 3 && ((hasLat && hasLng) || hasCommand || hasWpName);

            Log($"IsWaypointGrid - Name='{grid.Name}', Cols={grid.Columns.Count}, Match={isMatch}");

            return isMatch;
        }

        #endregion
    }
}
