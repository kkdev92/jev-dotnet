## Summary

Brief description of changes.

## Related Issue

Fixes #(issue number)

## Type of Change

- [ ] Bug fix (non-breaking change that fixes an issue)
- [ ] New feature (non-breaking change that adds functionality)
- [ ] Breaking change (fix or feature that would cause existing functionality to not work as expected)
- [ ] Contract change (a refreshed `spec/typesafe-v1/openapi.json`, or an edit to a companion file)
- [ ] Documentation update

## Changes

-
-

## Checklist

- [ ] I have read the [CONTRIBUTING](../CONTRIBUTING.md) guidelines
- [ ] Build is warning-free (`dotnet build src/Jev.slnx -c Release`)
- [ ] Tests pass (`dotnet test --solution src/Jev.slnx -c Release -- --filter-not-trait Category=Package`)
- [ ] A change in behaviour brings a test; a fix brings one seen to fail without the fix
      (see [CONTRIBUTING.md](../CONTRIBUTING.md#what-a-change-has-to-bring-with-it)) — or the
      pull request says why the change cannot be tested
- [ ] Formatting passes (`dotnet format src/Jev.slnx --verify-no-changes`)
- [ ] Generated sources are current (`dotnet run --project src/tools/Kkdev92.Jev.CodeGen -c Release -- verify`)
- [ ] No file under `Generated/` or `spec/typesafe-v1/openapi.json` was hand-edited, and TypeSafe's own document is not added anywhere
- [ ] Public API changes are reflected in `src/tests/PublicApi` and reviewed in this diff
- [ ] No API key, state, instructions, label, question id or answer reaches an exception message, a trace, a metric, or the `ToString()` of a result or an answer
- [ ] A claim of better performance comes with before-and-after numbers from the same machine
- [ ] I have updated documentation if needed
