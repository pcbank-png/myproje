namespace NSYazilim.Web.ViewModels
{
    public sealed class AdminVisitorStatisticsViewModel
    {
        public int SelectedDays { get; set; } = 30;
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public int TodayPageViews { get; set; }
        public int TodayUniqueVisitors { get; set; }
        public int MonthPageViews { get; set; }
        public int MonthUniqueVisitors { get; set; }
        public int PeriodPageViews { get; set; }
        public int PeriodUniqueVisitors { get; set; }
        public decimal PagesPerVisitor { get; set; }
        public int TodayGoogleRobotVisits { get; set; }
        public int TodayOtherRobotVisits { get; set; }
        public int TodayRobotVisits { get; set; }
        public int MonthGoogleRobotVisits { get; set; }
        public int MonthOtherRobotVisits { get; set; }
        public int MonthRobotVisits { get; set; }
        public int PeriodGoogleRobotVisits { get; set; }
        public int PeriodOtherRobotVisits { get; set; }
        public int PeriodRobotVisits { get; set; }
        public decimal RobotTrafficSharePercent { get; set; }
        public IReadOnlyList<RobotTrafficItem> RobotAgents { get; set; } = Array.Empty<RobotTrafficItem>();
        public IReadOnlyList<VisitorBreakdownItem> RobotTopPages { get; set; } = Array.Empty<VisitorBreakdownItem>();
        public IReadOnlyList<VisitorTrendItem> DailyTrend { get; set; } = Array.Empty<VisitorTrendItem>();
        public IReadOnlyList<VisitorTrendItem> MonthlyTrend { get; set; } = Array.Empty<VisitorTrendItem>();
        public IReadOnlyList<VisitorBreakdownItem> TopPages { get; set; } = Array.Empty<VisitorBreakdownItem>();
        public IReadOnlyList<VisitorBreakdownItem> Devices { get; set; } = Array.Empty<VisitorBreakdownItem>();
        public IReadOnlyList<VisitorBreakdownItem> Browsers { get; set; } = Array.Empty<VisitorBreakdownItem>();
        public IReadOnlyList<VisitorLocationItem> Countries { get; set; } = Array.Empty<VisitorLocationItem>();
        public IReadOnlyList<VisitorLocationItem> Cities { get; set; } = Array.Empty<VisitorLocationItem>();
        public int LocatedPageViews { get; set; }
        public decimal LocationCoveragePercent { get; set; }
        public int LocationBackfillPendingIpCount { get; set; }
        public int LocationBackfillUnresolvedIpCount { get; set; }
        public IReadOnlyList<RecentVisitItem> RecentVisits { get; set; } = Array.Empty<RecentVisitItem>();
    }

    public sealed class VisitorTrendItem
    {
        public string Label { get; set; } = string.Empty;
        public int PageViews { get; set; }
        public int UniqueVisitors { get; set; }
    }

    public sealed class RobotTrafficItem
    {
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int Count { get; set; }
        public decimal Percentage { get; set; }
    }

    public sealed class VisitorBreakdownItem
    {
        public string Label { get; set; } = string.Empty;
        public int Count { get; set; }
        public decimal Percentage { get; set; }
    }

    public sealed class VisitorLocationItem
    {
        public string Name { get; set; } = string.Empty;
        public string? Country { get; set; }
        public int UniqueVisitors { get; set; }
        public int TotalVisits { get; set; }
        public decimal Percentage { get; set; }
    }

    public sealed class RecentVisitItem
    {
        public string VisitorCode { get; set; } = string.Empty;
        public string Path { get; set; } = "/";
        public string Device { get; set; } = string.Empty;
        public string Browser { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string Location { get; set; } = "-";
        public DateTime VisitedAtUtc { get; set; }
    }
}
