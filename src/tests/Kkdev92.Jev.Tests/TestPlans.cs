namespace Kkdev92.Jev.Tests;

/// <summary>Plans the tests share, built the way an application would build them.</summary>
internal static class TestPlans
{
    public enum Department
    {
        Billing,
        Technical,
        Sales,
    }

    public static (JevDecisionPlan Plan, JevChoiceHandle<Department> Department, JevScoreHandle Frustration, JevNoulHandle Urgent) Triage()
    {
        var builder = new JevDecisionPlanBuilder();

        var department = builder.AddChoice<Department>(
            "department",
            "Which team should handle this?",
            [
                new(Department.Billing, "billing", "Payments, invoicing, refunds"),
                new(Department.Technical, "technical", "Bugs, outages, integrations"),
                new(Department.Sales, "sales", "Pricing, upgrades, new accounts"),
            ]);

        var frustration = builder.AddScore(
            "frustration",
            "How frustrated is the customer?",
            ["Calm", "Frustrated", "Very angry"]);

        var urgent = builder.AddNoul("is_urgent", "Does this convey urgency?");

        return (builder.Build(), department, frustration, urgent);
    }

    /// <summary>A response to <see cref="Triage"/>, in the shape of the API reference's examples.</summary>
    public static string TriageResponse() => new TestSupport.SystemOneResponseBuilder()
        .Choice("department", "billing", 0.81, ("billing", 0.88), ("technical", 0.12), ("sales", 0.0))
        .Score("frustration", 1.05, 0.92, ["Calm", "Frustrated", "Very angry"], 0.0, 0.95, 0.05)
        .Noul("is_urgent", 0.95)
        .Build();
}
