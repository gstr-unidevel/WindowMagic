using System.Collections.Generic;
using System.Linq;
using WindowMagic.Common.WinApiBridge;

namespace WindowMagic.Common
{
    public class DesktopDisplayMetrics
    {
        public string Key
        {
            get; private set;
        }

        public int NumberOfDisplays { get { return _monitorResolutions.Count; } }

        public void SetMonitor(int id, Display display)
        {
            if (!_monitorResolutions.ContainsKey(id) ||
                _monitorResolutions[id].ScreenWidth != display.ScreenWidth ||
                _monitorResolutions[id].ScreenHeight != display.ScreenHeight)
            {
                _monitorResolutions.Add(id, display);
                buildKey();
            }
        }

        private readonly Dictionary<int, Display> _monitorResolutions = new Dictionary<int, Display>();

        private void buildKey()
        {
            var keySegments = new List<string>();

            foreach (var entry in _monitorResolutions.OrderBy(row => row.Value.DeviceName))
            {
                keySegments.Add(string.Format("[DeviceName:{0} Loc:{1}x{2} Res:{3}x{4}]", entry.Value.DeviceName, entry.Value.Left, entry.Value.Top, entry.Value.ScreenWidth, entry.Value.ScreenHeight));
            }

            Key = string.Join(",", keySegments);
        }

        public override bool Equals(object obj)
        {
            if (!(obj is DesktopDisplayMetrics other)) return false;

            return this.Key == other.Key;
        }

        public override int GetHashCode()
        {
            return Key.GetHashCode();
        }

        public int GetHashCode(DesktopDisplayMetrics obj)
        {
            return obj.Key.GetHashCode();
        }
    }

    public interface IDesktopDisplayMetricsService
    {
        DesktopDisplayMetrics AcquireMetrics();
    }

    public class DesktopDisplayMetricsService : IDesktopDisplayMetricsService
    {
        private readonly IDisplayService _displayService;

        public DesktopDisplayMetricsService(IDisplayService displayService)
        {
            _displayService = displayService ?? throw new System.ArgumentNullException(nameof(displayService));
        }

        public DesktopDisplayMetrics AcquireMetrics()
        {
            var metrics = new DesktopDisplayMetrics();

            var displays = _displayService.GetDisplays();
            int displayId = 0;

            foreach (var display in displays)
            {
                metrics.SetMonitor(displayId++, display);
            }

            return metrics;
        }
    }
}