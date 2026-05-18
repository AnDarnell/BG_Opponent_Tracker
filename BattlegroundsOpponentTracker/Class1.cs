using HearthDb.Enums;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Enums;
using Hearthstone_Deck_Tracker.Plugins;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BattlegroundsTracker
{
    public class Plugin : IPlugin
    {
        public string Name => "Battlegrounds Opponent Tracker";
        public string Author => "Darnell/Brugdar";
        public Version Version => new Version(0, 5, 5);
        public string Description => "Kryssar mötta spelare + visar PID 1/2/3 med små siffror.";
        public string ButtonText => "Show/Hide debug window";
        public MenuItem MenuItem => null;

        private const int MaxFacedTracked = 5;
        private const int RecentFacedCountAfterDeath = 2;
        private const double XLeft = 270;
        private const double XTopFirst = 184;
        private const double SlotHeight = 93;
        private const double XSize = 60;
        private const double XLeftSlopePerSlot = -2.5;
        private const double PidOffsetX = 55;
        private const double PidOffsetY = 2;
        private const double GhostOffsetX = -8;
        private const double GhostOffsetY = 28;
        private const double DebugWidth = 275;
        private const bool FORCE_SHOW_ALL_X = false;
        private static readonly TimeSpan OverlayRefreshInterval = TimeSpan.FromMilliseconds(300);

        private static readonly Brush Pid1Brush = new SolidColorBrush(Color.FromRgb(255, 215, 60));
        private static readonly Brush Pid2Brush = new SolidColorBrush(Color.FromRgb(210, 225, 235));
        private static readonly Brush Pid3Brush = new SolidColorBrush(Color.FromRgb(205, 127, 50));
        private static readonly Brush XBrush = Brushes.Red;

        private string _lastOpponentName = null;
        private readonly HashSet<int> _metPlayerIds = new HashSet<int>();
        private readonly List<int> _metHistory = new List<int>();
        private readonly Dictionary<int, int> _metOnTurn = new Dictionary<int, int>();
        private bool _limitToRecentAfterDeath = false;
        private DateTime _lastOverlayRefresh = DateTime.MinValue;
        private readonly Dictionary<int, UIElement> _xBySlotIndex = new Dictionary<int, UIElement>();
        private readonly Dictionary<int, TextBlock> _pidLabelBySlotIndex = new Dictionary<int, TextBlock>();
        private readonly Dictionary<int, TextBlock> _ghostIconBySlotIndex = new Dictionary<int, TextBlock>();
        private readonly List<(int turn, int pid)> _ghostPerTurn = new List<(int turn, int pid)>();
        private int _lastRecordedTurn = -1;
        private int _pendingRecordedTurn = -1;
        private bool _pendingGhostRecord = false;
        private int _turnStartCount = 0;
        private readonly HashSet<int> _loggedTurns = new HashSet<int>();

        private static readonly string LogDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HearthstoneDeckTracker", "Plugins", "BattlegroundsTracker");
        private static readonly string GhostLogPath = System.IO.Path.Combine(LogDir, "ghost_log.csv");

        private int _matchId = 0;
        private TextBlock _debugTextBlock;
        private bool _showDebugOverlay = false;

        public void OnLoad()
        {
            GameEvents.OnGameStart.Add(OnGameStart);
            GameEvents.OnTurnStart.Add(OnTurnStart);
            Core.OverlayCanvas.Dispatcher.Invoke(() =>
            {
                EnsureOverlays();
                UpdateDebugText("Plugin loaded.");
            });
        }

        public void OnUnload()
        {
            Core.OverlayCanvas.Dispatcher.Invoke(() =>
            {
                foreach (var x in _xBySlotIndex.Values)
                    Core.OverlayCanvas.Children.Remove(x);
                foreach (var t in _pidLabelBySlotIndex.Values)
                    Core.OverlayCanvas.Children.Remove(t);
                foreach (var g in _ghostIconBySlotIndex.Values)
                    Core.OverlayCanvas.Children.Remove(g);
                if (_debugTextBlock != null)
                    Core.OverlayCanvas.Children.Remove(_debugTextBlock);
                _xBySlotIndex.Clear();
                _pidLabelBySlotIndex.Clear();
                _ghostIconBySlotIndex.Clear();
                _debugTextBlock = null;
            });
        }

        public void OnButtonPress()
        {
            _showDebugOverlay = !_showDebugOverlay;
            Core.OverlayCanvas.Dispatcher.Invoke(() =>
            {
                if (_debugTextBlock != null)
                    _debugTextBlock.Visibility = _showDebugOverlay
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            });
        }

        public void OnUpdate()
        {
            if (!Core.Game.IsBattlegroundsMatch)
                return;

            var now = DateTime.UtcNow;
            if (now - _lastOverlayRefresh > OverlayRefreshInterval)
            {
                _lastOverlayRefresh = now;
                Core.OverlayCanvas.Dispatcher.Invoke(UpdateOverlays);
            }

            try
            {
                var opponent = Core.Game.Entities.Values
                    .Where(e => e.IsOpponent && e.IsInPlay
                             && !string.IsNullOrEmpty(e.Name)
                             && e.Name != "Bartender Bob")
                    .FirstOrDefault();

                if (opponent == null)
                    return;

                var heroEntityId = opponent.GetTag(GameTag.HERO_ENTITY);
                if (heroEntityId <= 0 ||
                    !Core.Game.Entities.TryGetValue(heroEntityId, out var heroEntity))
                    return;

                var playerId = heroEntity.GetTag(GameTag.PLAYER_ID);
                if (playerId <= 0)
                    return;

                var entityKey = playerId.ToString();
                if (entityKey == _lastOpponentName)
                    return;

                _lastOpponentName = entityKey;
                RegisterMet(playerId);
                Core.OverlayCanvas.Dispatcher.Invoke(UpdateOverlays);
            }
            catch { }
        }

        private void OnGameStart()
        {
            if (!Core.Game.IsBattlegroundsMatch)
                return;

            _metPlayerIds.Clear();
            _metHistory.Clear();
            _metOnTurn.Clear();
            _limitToRecentAfterDeath = false;
            _lastOpponentName = null;
            _lastOverlayRefresh = DateTime.MinValue;
            _ghostPerTurn.Clear();
            _lastRecordedTurn = -1;
            _pendingRecordedTurn = -1;
            _pendingGhostRecord = false;
            _turnStartCount = 0;
            _loggedTurns.Clear();
            _matchId++;

            Core.OverlayCanvas.Dispatcher.Invoke(() =>
            {
                EnsureOverlays();
                HideAllXs();
                UpdateDebugText("New match!");
            });
        }

        private void OnTurnStart(ActivePlayer player)
        {
            if (!Core.Game.IsBattlegroundsMatch)
                return;

            _turnStartCount++;
            var turn = Core.Game.GetTurnNumber();

            if (turn == _lastRecordedTurn)
            {
                Core.OverlayCanvas.Dispatcher.Invoke(UpdateOverlays);
                return;
            }

            if (!_limitToRecentAfterDeath && !HasAnyDeadPlayerInLobby())
            {
                Core.OverlayCanvas.Dispatcher.Invoke(UpdateOverlays);
                return;
            }

            if (_pendingGhostRecord && _pendingRecordedTurn >= 0 && !_loggedTurns.Contains(_pendingRecordedTurn))
            {
                _ghostPerTurn.Add((_pendingRecordedTurn, 0));
                LogGhostEvent(_matchId, _pendingRecordedTurn, 0, new List<int>(), "(timed out)");
                _loggedTurns.Add(_pendingRecordedTurn);
            }

            _pendingRecordedTurn = turn;
            _lastRecordedTurn = turn;
            _pendingGhostRecord = true;

            Core.OverlayCanvas.Dispatcher.Invoke(UpdateOverlays);
        }

        private void RegisterMet(int playerId)
        {
            if (playerId <= 0)
                return;

            _metHistory.Remove(playerId);
            _metHistory.Add(playerId);

            while (_metHistory.Count > MaxFacedTracked)
                _metHistory.RemoveAt(0);

            _metPlayerIds.Clear();
            foreach (var pid in _metHistory)
                _metPlayerIds.Add(pid);

            _metOnTurn[playerId] = Core.Game.GetTurnNumber();
        }

        private static bool HasValidHpTags(Entity e)
        {
            return e.HasTag(GameTag.HEALTH) && e.GetTag(GameTag.HEALTH) > 0;
        }

        private static int GetCurrentHp(Entity e)
        {
            return e.GetTag(GameTag.HEALTH)
                 + e.GetTag(GameTag.ARMOR)
                 - e.GetTag(GameTag.DAMAGE);
        }

        private static bool IsDeadOrGhost(Entity e)
        {
            if (!HasValidHpTags(e))
                return false;
            return GetCurrentHp(e) <= 0;
        }

        private bool HasAnyDeadPlayerInLobby()
        {
            return Core.Game.Entities.Values
                .Where(e => e.HasTag(GameTag.PLAYER_LEADERBOARD_PLACE))
                .Any(IsDeadOrGhost);
        }

        private void EnsureOverlays()
        {
            if (_xBySlotIndex.Count > 0)
                return;

            for (int slot = 1; slot <= 8; slot++)
            {
                var x = CreateX(XSize, 6);
                _xBySlotIndex[slot] = x;
                Core.OverlayCanvas.Children.Add(x);

                var label = new TextBlock
                {
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
                    Padding = new Thickness(3, 0, 3, 0),
                    Visibility = Visibility.Collapsed,
                    IsHitTestVisible = false
                };
                _pidLabelBySlotIndex[slot] = label;
                Core.OverlayCanvas.Children.Add(label);

                var ghost = new TextBlock
                {
                    Text = "G",
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromArgb(230, 80, 0, 150)),
                    Padding = new Thickness(4, 2, 4, 2),
                    Visibility = Visibility.Collapsed,
                    IsHitTestVisible = false
                };
                _ghostIconBySlotIndex[slot] = ghost;
                Core.OverlayCanvas.Children.Add(ghost);
            }

            _debugTextBlock = new TextBlock
            {
                Foreground = Brushes.Yellow,
                FontSize = 11,
                Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                TextWrapping = TextWrapping.Wrap,
                Width = DebugWidth,
                IsHitTestVisible = false,
                Visibility = _showDebugOverlay ? Visibility.Visible : Visibility.Collapsed
            };

            Canvas.SetLeft(_debugTextBlock, Core.OverlayCanvas.ActualWidth - DebugWidth - 10);
            Canvas.SetBottom(_debugTextBlock, 125);
            Core.OverlayCanvas.Children.Add(_debugTextBlock);
        }

        private UIElement CreateX(double size, double thickness)
        {
            var c = new Canvas { Width = size, Height = size, IsHitTestVisible = false, Opacity = 0.9 };

            c.Children.Add(new Line
            {
                X1 = 0,
                Y1 = 0,
                X2 = size,
                Y2 = size,
                Stroke = XBrush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });

            c.Children.Add(new Line
            {
                X1 = size,
                Y1 = 0,
                X2 = 0,
                Y2 = size,
                Stroke = XBrush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });

            return c;
        }

        private void HideAllXs()
        {
            foreach (var x in _xBySlotIndex.Values)
                x.Visibility = Visibility.Collapsed;
        }

        private void UpdateDebugText(string text)
        {
            if (_debugTextBlock == null)
                return;
            _debugTextBlock.Text = text;
        }

        private void UpdateOverlays()
        {
            EnsureOverlays();

            if (!_limitToRecentAfterDeath && HasAnyDeadPlayerInLobby())
                _limitToRecentAfterDeath = true;

            var recentSet = _limitToRecentAfterDeath
                ? new HashSet<int>(_metHistory.Skip(Math.Max(0, _metHistory.Count - RecentFacedCountAfterDeath)))
                : null;

            var currentTurn = Core.Game.GetTurnNumber();

            if (_pendingGhostRecord)
            {
                var livingPlayers = Core.Game.Entities.Values
                    .Where(e => e.HasTag(GameTag.PLAYER_LEADERBOARD_PLACE) &&
                                e.GetTag(GameTag.BACON_DUMMY_PLAYER) != 1 &&
                                !IsDeadOrGhost(e))
                    .ToList();

                bool isOddCount = livingPlayers.Count % 2 != 0;
                bool timedOut = currentTurn > _pendingRecordedTurn + 1;

                var last3GhostAtRecord = _ghostPerTurn.Skip(Math.Max(0, _ghostPerTurn.Count - 3))
                                                       .Select(g => g.pid).ToList();

                var oddPlayer = livingPlayers.FirstOrDefault(e => e.GetTag(GameTag.BACON_ODD_PLAYER_OUT) == 1);
                int candidateGhostPid = oddPlayer?.GetTag(GameTag.PLAYER_ID) ?? 0;

                bool tagIsStale = candidateGhostPid > 0 && last3GhostAtRecord.Contains(candidateGhostPid);
                bool oddPlayerFound = oddPlayer != null && !tagIsStale;

                if (oddPlayerFound || timedOut)
                {
                    if (tagIsStale && timedOut)
                        oddPlayer = null;

                    var bottom3Pids = livingPlayers
                        .OrderBy(e => GetCurrentHp(e))
                        .Take(3)
                        .Select(e => e.GetTag(GameTag.PLAYER_ID))
                        .ToHashSet();

                    var eligiblePids = isOddCount
                        ? livingPlayers
                            .Where(e => {
                                var pid = e.GetTag(GameTag.PLAYER_ID);
                                return bottom3Pids.Contains(pid) && !last3GhostAtRecord.Contains(pid);
                            })
                            .Select(e => e.GetTag(GameTag.PLAYER_ID))
                            .ToList()
                        : new List<int>();

                    int ghostPid = oddPlayer?.GetTag(GameTag.PLAYER_ID) ?? 0;

                    if (isOddCount && !_loggedTurns.Contains(_pendingRecordedTurn))
                    {
                        _ghostPerTurn.Add((_pendingRecordedTurn, ghostPid));
                        LogGhostEvent(_matchId, _pendingRecordedTurn, ghostPid, eligiblePids);
                        _loggedTurns.Add(_pendingRecordedTurn);
                    }
                    else if (!isOddCount)
                    {
                        _ghostPerTurn.Add((_pendingRecordedTurn, 0));
                    }

                    _pendingGhostRecord = false;
                }
            }

            var ordered = Core.Game.Entities.Values
                .Where(e => e.HasTag(GameTag.PLAYER_ID)
                         && e.GetTag(GameTag.PLAYER_ID) > 0
                         && e.HasTag(GameTag.PLAYER_LEADERBOARD_PLACE))
                .OrderBy(e => e.GetTag(GameTag.PLAYER_LEADERBOARD_PLACE))
                .ToList();

            for (int slot = 1; slot <= 8; slot++)
            {
                if (!_xBySlotIndex.TryGetValue(slot, out var x)) continue;
                if (!_pidLabelBySlotIndex.TryGetValue(slot, out var label)) continue;
                if (!_ghostIconBySlotIndex.TryGetValue(slot, out var ghostIcon)) continue;

                var left = XLeft + (slot - 1) * XLeftSlopePerSlot;
                var top = XTopFirst + (slot - 1) * SlotHeight;

                Canvas.SetLeft(x, left);
                Canvas.SetTop(x, top);
                Canvas.SetLeft(label, left + PidOffsetX);
                Canvas.SetTop(label, top + PidOffsetY);
                label.Visibility = Visibility.Collapsed;
                Canvas.SetLeft(ghostIcon, left + GhostOffsetX);
                Canvas.SetTop(ghostIcon, top + GhostOffsetY);
                ghostIcon.Visibility = Visibility.Collapsed;

                if (slot > ordered.Count)
                {
                    x.Visibility = Visibility.Collapsed;
                    continue;
                }

                var entity = ordered[slot - 1];
                var pid = entity.GetTag(GameTag.PLAYER_ID);

                if (pid >= 1 && pid <= 3)
                {
                    label.Text = pid.ToString();
                    label.Foreground = pid == 1 ? Pid1Brush : pid == 2 ? Pid2Brush : Pid3Brush;
                    label.Visibility = Visibility.Visible;
                }

                var playerEntity = Core.Game.Entities.Values
                    .FirstOrDefault(e => e.HasTag(GameTag.PLAYER_LEADERBOARD_PLACE) &&
                                         e.GetTag(GameTag.PLAYER_ID) == pid &&
                                         e.GetTag(GameTag.BACON_DUMMY_PLAYER) != 1 &&
                                         e.HasTag(GameTag.BACON_ODD_PLAYER_OUT));

                bool isOddOut = playerEntity != null && playerEntity.GetTag(GameTag.BACON_ODD_PLAYER_OUT) == 1;
                ghostIcon.Visibility = (isOddOut && !entity.IsPlayer) ? Visibility.Visible : Visibility.Collapsed;

                if (FORCE_SHOW_ALL_X)
                {
                    x.Visibility = Visibility.Visible;
                    continue;
                }

                if (entity.IsPlayer || IsDeadOrGhost(entity))
                {
                    x.Visibility = Visibility.Collapsed;
                    continue;
                }

                if (_metOnTurn.TryGetValue(pid, out var turnMet) && turnMet > currentTurn)
                {
                    x.Visibility = Visibility.Collapsed;
                    continue;
                }

                bool show = _limitToRecentAfterDeath
                    ? recentSet.Contains(pid)
                    : _metPlayerIds.Contains(pid);

                x.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }

            if (!_showDebugOverlay || _debugTextBlock == null)
                return;

            var debugLines = new List<string>();
            debugLines.Add($"Leaderboard ({ordered.Count}):");

            foreach (var e in ordered)
            {
                var place = e.GetTag(GameTag.PLAYER_LEADERBOARD_PLACE);
                var pid = e.GetTag(GameTag.PLAYER_ID);
                var hp = e.GetTag(GameTag.HEALTH) + e.GetTag(GameTag.ARMOR) - e.GetTag(GameTag.DAMAGE);
                bool met = _metPlayerIds.Contains(pid);
                bool dead = IsDeadOrGhost(e);
                debugLines.Add(
                    $"#{place} HP:{hp} PID:{pid}" +
                    $"{(dead ? " DEAD" : "")}{(met ? " ✕" : "")}{(e.IsPlayer ? " (You)" : "")}"
                );
            }

            debugLines.Add($"RecentFacedMode: {_limitToRecentAfterDeath}");
            debugLines.Add($"Faced: {string.Join(", ", _metHistory)}");
            debugLines.Add($"GhostPending: {_pendingGhostRecord} (turn: {_pendingRecordedTurn})");
            debugLines.Add($"LoggedTurns: {string.Join(", ", _loggedTurns.OrderBy(t => t))}");

            var ghostStr = _ghostPerTurn.Count > 0
                ? string.Join(", ", _ghostPerTurn.Select(g => $"T{g.turn}:{(g.pid == 0 ? "-" : g.pid.ToString())}"))
                : "(none)";
            debugLines.Add($"Ghost/turn: {ghostStr}");

            var livingForDebug = Core.Game.Entities.Values
                .Where(e => !e.IsPlayer &&
                            e.HasTag(GameTag.PLAYER_LEADERBOARD_PLACE) &&
                            e.GetTag(GameTag.BACON_DUMMY_PLAYER) != 1 &&
                            !IsDeadOrGhost(e))
                .ToList();

            bool isOddNow = livingForDebug.Count % 2 != 0;
            debugLines.Add($"Living (excl. you): {livingForDebug.Count} → {(isOddNow ? "ODD" : "EVEN")}");

            if (isOddNow)
            {
                var bottom3Hp = livingForDebug.OrderBy(e => GetCurrentHp(e)).Take(3)
                    .Select(e => e.GetTag(GameTag.PLAYER_ID)).ToHashSet();
                var last3Ghost = _ghostPerTurn.Skip(Math.Max(0, _ghostPerTurn.Count - 3))
                    .Select(g => g.pid).ToList();

                foreach (var e in ordered.Where(e => e.GetTag(GameTag.BACON_DUMMY_PLAYER) != 1 && !e.IsPlayer))
                {
                    var pid = e.GetTag(GameTag.PLAYER_ID);
                    var oddOut = e.GetTag(GameTag.BACON_ODD_PLAYER_OUT);
                    bool eligible = !last3Ghost.Contains(pid) && bottom3Hp.Contains(pid);
                    debugLines.Add($"pid {pid}: ODD_OUT={oddOut} eligible={eligible}");
                }
            }

            UpdateDebugText(string.Join("\n", debugLines));
        }

        private void LogGhostEvent(int matchId, int turn, int ghostPid, List<int> eligiblePids, string note = "")
        {
            try
            {
                if (!Directory.Exists(LogDir))
                    Directory.CreateDirectory(LogDir);

                int eligibleCount = eligiblePids.Count;
                double prob = eligibleCount > 0 ? 1.0 / eligibleCount : 0;
                string eligibleStr = string.Join("|", eligiblePids);
                bool isNew = !File.Exists(GhostLogPath);

                using (var sw = File.AppendText(GhostLogPath))
                {
                    if (isNew)
                        sw.WriteLine("Date,MatchId,Turn,GhostPid,EligiblePids,EligibleCount,Probability,Note");
                    sw.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{matchId},{turn},{ghostPid},{eligibleStr},{eligibleCount},{prob:F2},{note}");
                }
            }
            catch { }
        }
    }
}