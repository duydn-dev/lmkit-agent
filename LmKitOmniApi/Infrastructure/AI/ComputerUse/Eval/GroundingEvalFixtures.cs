namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Eval;

/// <summary>
/// A small, self-contained set of default grounding-eval cases so the harness is useful with
/// no request body: each pairs a realistic accessibility observation (a numbered element
/// list) with an unambiguous "click X" goal and the ground-truth ref. They are deliberately
/// benign navigation/search/cart targets (never credential or CAPTCHA elements) so the
/// harness measures pure grounding, not the safety refusals. Operators can POST their own
/// case list to measure against their real pages.
/// </summary>
public static class GroundingEvalFixtures
{
    /// <summary>The built-in default fixture cases (a fresh list on each call).</summary>
    public static IReadOnlyList<GroundingEvalCase> Default() => new List<GroundingEvalCase>
    {
        // 1. Top-nav: pick the right menu item among siblings.
        new(
            TaskGoal: "Open the Pricing page.",
            Observation: new ComputerUseObservation
            {
                Url = "https://example.com/",
                Title = "Acme — Home",
                Elements = new[]
                {
                    new InteractiveElement(1, "link", "Home", null),
                    new InteractiveElement(2, "link", "Pricing", null),
                    new InteractiveElement(3, "link", "Docs", null),
                    new InteractiveElement(4, "link", "Contact", null),
                },
            },
            ExpectedRef: 2),

        // 2. Search results: pick the specific result, not the search box or other results.
        new(
            TaskGoal: "Click the search result titled \"Best Laptops 2026\".",
            Observation: new ComputerUseObservation
            {
                Url = "https://example.com/search?q=laptops",
                Title = "Search results",
                Elements = new[]
                {
                    new InteractiveElement(1, "textbox", "Search", "laptops"),
                    new InteractiveElement(2, "link", "Best Laptops 2026", null),
                    new InteractiveElement(3, "link", "Cheap Laptops Under $500", null),
                    new InteractiveElement(4, "button", "Next page", null),
                },
            },
            ExpectedRef: 2),

        // 3. Form: pick the Submit control, not Cancel or the fields.
        new(
            TaskGoal: "Submit the contact form.",
            Observation: new ComputerUseObservation
            {
                Url = "https://example.com/contact",
                Title = "Contact us",
                Elements = new[]
                {
                    new InteractiveElement(1, "textbox", "Your name", null),
                    new InteractiveElement(2, "textbox", "Your email", null, "email"),
                    new InteractiveElement(3, "textbox", "Message", null),
                    new InteractiveElement(4, "button", "Submit", null),
                    new InteractiveElement(5, "button", "Cancel", null),
                },
            },
            ExpectedRef: 4),

        // 4. Multiple equally-correct targets: two "Add to cart" buttons on one product page.
        new(
            TaskGoal: "Add this product to the cart.",
            Observation: new ComputerUseObservation
            {
                Url = "https://example.com/product/42",
                Title = "Wireless Headphones",
                Elements = new[]
                {
                    new InteractiveElement(5, "button", "Add to cart", null),
                    new InteractiveElement(6, "button", "Buy now", null),
                    new InteractiveElement(7, "link", "Back to results", null),
                    new InteractiveElement(9, "button", "Add to cart", null), // sticky footer duplicate
                },
            },
            ExpectedRef: 5,
            AcceptableRefs: new[] { 5, 9 }),

        // 5. Go to the cart among header icons.
        new(
            TaskGoal: "Go to the shopping cart.",
            Observation: new ComputerUseObservation
            {
                Url = "https://example.com/product/42",
                Title = "Wireless Headphones",
                Elements = new[]
                {
                    new InteractiveElement(1, "button", "Menu", null),
                    new InteractiveElement(2, "link", "Cart (3)", null),
                    new InteractiveElement(3, "link", "My account", null),
                },
            },
            ExpectedRef: 2),
    };
}
