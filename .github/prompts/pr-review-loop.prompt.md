---
description: "Open a GitHub PR and iterate with Copilot code review bot until approved. Use when: opening a pull request, code review loop, CR loop, PR review cycle."
agent: "agent"
---

Open a pull request for the current branch, request a code review from the Copilot pull request reviewer bot, and iterate until the review passes with no actionable comments.

## Step 1: Create the PR

```bash
gh pr create --base master --head $(git branch --show-current) \
  --title "<concise title>" \
  --body "<markdown body with ## Summary, ### Changes, ### Testing sections>"
```

- Infer the title and body from the commits on the branch (use `git log master..HEAD --oneline`).
- If the branch targets a different base than `master`, adjust `--base` accordingly.
- Capture the PR number from the output.

## Step 2: Request Copilot review

```bash
gh pr edit <PR_NUMBER> --add-reviewer "copilot-pull-request-reviewer"
```

## Step 3: Poll for review completion

**Poll every 30 seconds**, up to 20 attempts. Do NOT poll more frequently than 30s.

Check whether the review is done by counting Copilot reviews:

```bash
count=$(gh api repos/bill-long/COHAD/pulls/<PR>/reviews \
  --jq '[.[] | select(.user.login == "copilot-pull-request-reviewer[bot]")] | length')
```

When the count increases from the previous known count, the new review has arrived. Break out of the loop.

If 20 attempts pass with no new review, stop and tell the user.

## Step 4: Read the review

Get the latest Copilot review:

```bash
gh api repos/bill-long/COHAD/pulls/<PR>/reviews \
  --jq '[.[] | select(.user.login == "copilot-pull-request-reviewer[bot]")] | last | "REVIEW_ID=\(.id) STATE=\(.state)\nBODY=\(.body)"'
```

Then read inline comments for that review:

```bash
gh api "repos/bill-long/COHAD/pulls/<PR>/reviews/<REVIEW_ID>/comments" \
  --jq '.[] | "ID=\(.id) FILE=\(.path) LINE=\(.line // .original_line)\nBODY=\(.body)\n---"'
```

If the review state is `APPROVED` with no inline comments, the loop is done — tell the user and stop.

## Step 5: Address each comment

For every actionable inline comment:

1. Read the relevant code and fix the issue.
2. Build to verify the fix compiles:
   - Backend changes: `dotnet build Web/Web.csproj`
   - Frontend changes: `cd Web/ClientApp && npx ng build`
   - Run unit tests if the change is non-trivial: `dotnet test Web.UnitTests/Web.UnitTests.csproj` or `cd Web/ClientApp && npx ng test --no-watch --browsers=ChromeHeadless`
3. Stage, commit, and push:
   ```bash
   git add <files> && git commit -m "<descriptive message>" && git push
   ```
4. Reply to each inline comment with the fix:
   ```bash
   gh api repos/bill-long/COHAD/pulls/<PR>/comments/<COMMENT_ID>/replies \
     -f body="Fixed in <short-hash>. <brief description of what changed>"
   ```

## Step 6: Re-request review and repeat

After all comments are addressed:

```bash
gh api repos/bill-long/COHAD/pulls/<PR>/requested_reviewers \
  -f "reviewers[]=copilot-pull-request-reviewer[bot]"
```

Then go back to **Step 3** (poll for the next review). Repeat until the review passes cleanly.

## Important rules

- **Polling interval: 30 seconds minimum.** Never poll faster than this.
- **Max poll attempts per round: 20.** Stop and inform the user if exceeded.
- **Reply convention:** Always include the short commit hash and a brief description in reply bodies.
- **Do not skip verification.** Always build (and test when appropriate) before pushing fixes.
- **Stop when approved** or when there are no actionable comments remaining.
