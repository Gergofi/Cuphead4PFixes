using System;
using System.Collections.Generic;
using System.Text;

namespace Cuphead4PFixes
{
    internal sealed class JoinControllerPress
    {
        internal int Id;
        internal int ButtonCount;
        internal ulong Down;
        internal ulong Held;
        internal bool Assigned;
        internal bool AnyButtonDown;
        internal object Connection;
    }

    // Sample every Update, including frames with no join press. A join waits
    // briefly so a leading copy cannot beat its owner's input. Observed mirror
    // pairs survive individual missing/delayed edges. Two isolated press/release
    // cycles can establish that coincident controllers actually are independent.
    internal sealed class JoinPressPolicy
    {
        internal const double EchoWindow = 0.20;
        internal const double DecisionDelay = 0.25;
        internal const double QuietWindow = 0.35;
        private const double Expiry = 2.0;

        private sealed class Pulse
        {
            internal double At;
            internal double ReleasedAt = double.PositiveInfinity;
            internal ulong Down;
            internal ulong Held;
            internal int Requesters;
            internal bool Consumed;
        }

        private sealed class Device
        {
            internal JoinControllerPress Sample;
            internal double LastDownAt = double.NegativeInfinity;
            internal double LastActiveAt = double.NegativeInfinity;
            internal ulong LastDown;
            internal Pulse Pending;
            // Peer ID -> isolated presses witnessed since the last echo.
            internal readonly Dictionary<int, int> Peers = new Dictionary<int, int>();
        }

        private readonly Dictionary<int, Device> _devices = new Dictionary<int, Device>();
        private readonly List<int> _order = new List<int>();
        private double _now;
        internal ulong SelectedDown;
        internal ulong SelectedHeld;

        internal void Reset()
        {
            _devices.Clear();
            _order.Clear();
        }

        internal void CancelPending()
        {
            foreach (var device in _devices.Values) device.Pending = null;
        }

        // Emitted only for a join decision, not on every gameplay button press.
        internal string DescribeInput()
        {
            var text = new StringBuilder();
            foreach (var id in _order)
            {
                var device = _devices[id];
                text.Append(" input{").Append(id).Append(" down=0x").Append(device.Sample.Down.ToString("X"))
                    .Append(" held=0x").Append(device.Sample.Held.ToString("X"))
                    .Append(" lastDown=0x").Append(device.LastDown.ToString("X"))
                    .Append(" ageMs=").Append(double.IsNegativeInfinity(device.LastDownAt)
                        ? "none" : ((_now - device.LastDownAt) * 1000).ToString("F0"))
                    .Append("}");
            }
            return text.ToString();
        }

        internal void Observe(double now, IList<JoinControllerPress> presses, Action<int, int> reportPair = null)
        {
            if (now < _now) Reset();
            _now = now;
            var removed = new List<int>();
            foreach (var entry in _devices)
            {
                JoinControllerPress current = null;
                foreach (var press in presses)
                    if (press.Id == entry.Key) { current = press; break; }
                if (current == null || !ReferenceEquals(current.Connection, entry.Value.Sample.Connection))
                    removed.Add(entry.Key);
            }
            foreach (var id in removed)
            {
                _devices.Remove(id);
                foreach (var device in _devices.Values) device.Peers.Remove(id);
            }

            _order.Clear();
            foreach (var press in presses)
            {
                Device device;
                if (!_devices.TryGetValue(press.Id, out device))
                    _devices[press.Id] = device = new Device();
                device.Sample = press;
                _order.Add(press.Id);
                if (press.AnyButtonDown || press.Held != 0) device.LastActiveAt = now;
                if (press.AnyButtonDown)
                {
                    device.LastDown = press.Down;
                    device.LastDownAt = now;
                    if (device.Pending == null || device.Pending.Consumed || now - device.Pending.At > Expiry)
                        device.Pending = new Pulse { At = now, Down = press.Down, Held = press.Held };
                }
                if (device.Pending != null && press.Held == 0 && double.IsPositiveInfinity(device.Pending.ReleasedAt))
                    device.Pending.ReleasedAt = now;
            }

            foreach (var id in _order)
            {
                var device = _devices[id];
                if (!device.Sample.AnyButtonDown || device.LastDown == 0 || device.Sample.ButtonCount > 64) continue;
                foreach (var otherId in _order)
                {
                    if (id == otherId) continue;
                    var other = _devices[otherId];
                    if (device.Sample.ButtonCount != other.Sample.ButtonCount || device.LastDown != other.LastDown ||
                        now - other.LastDownAt > EchoWindow + 0.001) continue;
                    var newPair = !device.Peers.ContainsKey(otherId);
                    device.Peers[otherId] = 0;
                    other.Peers[id] = 0;
                    if (newPair && reportPair != null) reportPair(id, otherId);
                }
            }
        }

        internal int Select(int requester, Func<int, int, bool> ownedByOther,
            Func<JoinControllerPress, bool> allowed, bool filterEchoes, Action<int, int> reportEcho)
        {
            foreach (var id in _order)
            {
                var device = _devices[id];
                var sample = device.Sample;
                if (!allowed(sample) || ownedByOther(id, requester)) continue;
                var pulse = device.Pending;
                if (pulse == null || pulse.Consumed || _now - pulse.At > Expiry) continue;
                // Only a slot eligible at the actual press may receive the
                // delayed event. Menu presses cannot be replayed on map load.
                if (_now == pulse.At) pulse.Requesters |= 1 << requester;
                if ((pulse.Requesters & (1 << requester)) == 0) continue;
                if (!filterEchoes)
                {
                    if (!sample.AnyButtonDown) continue;
                }
                else
                {
                    if (_now - pulse.At < DecisionDelay) continue;
                    var blockingOwner = -1;
                    var activeOwner = -1;
                    var recovered = new List<int>();
                    var isolated = new List<int>();
                    var awaitingRelease = false;
                    foreach (var peer in device.Peers)
                    {
                        if (!ownedByOther(peer.Key, requester)) continue;
                        var other = _devices[peer.Key];
                        if (other.LastActiveAt >= pulse.At - QuietWindow)
                        {
                            blockingOwner = peer.Key;
                            activeOwner = peer.Key;
                            break;
                        }
                        // Independence requires a complete stroke and a quiet
                        // interval AFTER release, not merely one missing edge.
                        if (_now - pulse.ReleasedAt < EchoWindow)
                        {
                            awaitingRelease = true;
                            continue;
                        }
                        isolated.Add(peer.Key);
                        if (peer.Value < 1) blockingOwner = peer.Key;
                        else recovered.Add(peer.Key);
                    }
                    if (awaitingRelease && blockingOwner < 0) continue;
                    foreach (var peer in isolated) device.Peers[peer]++;
                    if (activeOwner >= 0) device.Peers[activeOwner] = 0;
                    if (blockingOwner >= 0)
                    {
                        pulse.Consumed = true;
                        if (reportEcho != null) reportEcho(id, blockingOwner);
                        continue;
                    }
                    foreach (var peer in recovered)
                    {
                        device.Peers.Remove(peer);
                        _devices[peer].Peers.Remove(id);
                    }
                }
                pulse.Consumed = true;
                SelectedDown = pulse.Down;
                SelectedHeld = pulse.Held;
                return id;
            }
            return -1;
        }
    }
}
