# Contributing to Marketlens

Thanks for helping. Issues and pull requests are welcome.

## Before you start

- For anything larger than a small fix, open an issue first so we can agree on the approach.
- Read [docs/CONTEXT.md](docs/CONTEXT.md) for the glossary and where things live, and
  [docs/architecture.md](docs/architecture.md) for how the parts fit together.

## Ground rules

- **The server decides.** Prices, conversions, alert checks and permissions are computed in the
  API. The web and mobile apps display and format; they never call data providers.
- **One owner per responsibility.** New code goes into the feature folder that already owns the
  subject. Adding a market data provider means one new adapter; adding a language means one new
  messages file.
- **Money is never a binary float.** Use `decimal` in C#, `Decimal` in Python, and strings in JSON
  and TypeScript (parsed only for display).
- **No secrets in the repository.** Configuration comes from environment variables; add every new
  one to `.env.example` with a placeholder.
- **Tests come with the change.** A bug fix comes with the test that would have caught it.

## Making a change

1. Fork and create a branch: `feature/<short-name>` or `fix/<short-name>`.
2. Run the checks from the README for every part you touched. CI runs them too.
3. Write commit messages in the imperative that say why: `Reject alerts for unknown assets`.
4. Open a pull request describing what changed, why, and how you tested it. Include screenshots
   for UI changes.

## Code style

- TypeScript: ESLint (typescript-eslint strict) and Prettier, `npm run lint`.
- C#: `dotnet format`; analyzer warnings fail the build.
- Python: Ruff for linting and formatting, mypy in strict mode.

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE).
