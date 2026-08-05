using System;
using System.Windows.Forms;

namespace MissionHelperExtension
{
    /// <summary>
    /// Feature: Adds "Dist Total" column showing cumulative distance
    /// </summary>
    public class DistanceTotalFeature
    {
        private const string COL_DIST_TOTAL = "DistTotal";

        private readonly DataGridView _grid;
        private readonly Action<string> _log;
        private bool _columnAdded;

        public DistanceTotalFeature(DataGridView grid, Action<string> log)
        {
            _grid = grid;
            _log = log;
        }

        public void Initialize()
        {
            _log("DistanceTotalFeature: Initializing...");

            AddColumn();

            _grid.CellValueChanged += OnGridChanged;
            _grid.RowsAdded += OnGridChanged;
            _grid.RowsRemoved += OnGridChanged;

            _log("DistanceTotalFeature: Initialized");
        }

        public void Cleanup()
        {
            if (_grid != null)
            {
                _grid.CellValueChanged -= OnGridChanged;
                _grid.RowsAdded -= OnGridChanged;
                _grid.RowsRemoved -= OnGridChanged;
            }
        }

        private void AddColumn()
        {
            if (_columnAdded || _grid == null) return;

            // Idempotent: if the column is already present (e.g. the plugin re-ran its
            // setup), adopt it instead of adding a duplicate, which would throw and
            // leave the whole feature setup half-initialized.
            if (_grid.Columns.Contains(COL_DIST_TOTAL))
            {
                _columnAdded = true;
                RecalculateDistances();
                return;
            }

            var col = new DataGridViewTextBoxColumn
            {
                Name = COL_DIST_TOTAL,
                HeaderText = "Dist Total (m)",
                Width = 110,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleRight,
                    Format = "N0"
                }
            };

            _grid.Columns.Add(col);
            _columnAdded = true;

            _log("DistanceTotalFeature: Column added");
            RecalculateDistances();
        }

        private void OnGridChanged(object sender, EventArgs e)
        {
            RecalculateDistances();
        }

        private void RecalculateDistances()
        {
            if (_grid == null || !_columnAdded || _grid.RowCount == 0)
                return;

            try
            {
                string distColName = FindDistColumn();
                if (distColName == null) return;

                double totalDist = 0;

                for (int i = 0; i < _grid.RowCount; i++)
                {
                    var row = _grid.Rows[i];
                    double legDist = GetCellDouble(row, distColName);
                    totalDist += legDist;
                    row.Cells[COL_DIST_TOTAL].Value = totalDist;
                }
            }
            catch { }
        }

        private string FindDistColumn()
        {
            string[] names = { "Dist", "dist", "Distance" };
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
                if (double.TryParse(strVal, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double result))
                {
                    return result;
                }
            }
            catch { }
            return 0;
        }
    }
}
