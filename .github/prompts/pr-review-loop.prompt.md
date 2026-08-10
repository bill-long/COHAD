---
description: "Open a GitHub PR and iterate with Copilot code review bot until approved. Use when: opening a pull request, code review loop, CR loop, PR review cycle."
agent: "agent"
---

Open a pull request for the current branch, request a code review from the Copilot pull request reviewer bot, and iterate until the review passes with no actionable comments.

## Step 1: Create the PR

```bash
gh pr create --base main --head $(git branch --show-current) \
  --title "<concise title>" \
  --body "<markdown body with ## Summary, ### Changes, ### Testing sections>"
```

- Infer the title and body from the commits on the branch (use `git log main..HEAD --oneline`).
- If the branch targets a different base than `main`, adjust `--base` accordingly.
- Capture the PR number from the output.

## Step 2: Request Copilot review

```bash
gh pr edit <PR_NUMBER> --add-reviewer "copilot-pull-request-reviewer"
```

## Step 3: Poll for review completion

**Poll every 30 seconds**, up to 20 attempts. Do NOT poll more frequently than 30s.

Copilot may post comments under **either** `copilot-pull-request-reviewer[bot]` or `Copilot` as the user login. Check for both when polling for new reviews and reading comments.

Check whether new comments have arrived by counting all Copilot-authored PR comments:

```bash
count=$(gh api "repos/bill-long/COHAD/pulls/<PR>/comments?per_page=100" \
  --jq '[.[] | select(.user.login == "copilot-pull-request-reviewer[bot]" or .user.login == "Copilot")] | length')
```

When the count increases from the previous known count, new review comments have arrived. Break out of the loop.

If 20 attempts pass with no new comments, stop and tell the user.

## Step 4: Read the review

Get new Copilot comments (from both logins), sorted oldest-first:

```bash
gh api "repos/bill-long/COHAD/pulls/<PR>/comments?per_page=100&sort=created&direction=desc" \
  --jq '[.[] | select((.user.login == "copilot-pull-request-reviewer[bot]" or .user.login == "Copilot") and (.created_at > "<LAST_SEEN_TIMESTAMP>"))] | reverse | .[] | "ID=\(.id) FILE=\(.path) LINE=\(.line // .original_line)\nBODY=\(.body)\n---"'
```

Track the timestamp of the last comment you've seen so you only process new ones each round.

Also check formal reviews for an APPROVED state:

```bash
gh api repos/bill-long/COHAD/pulls/<PR>/reviews \
  --jq '[.[] | select(.user.login == "copilot-pull-request-reviewer[bot]" or .user.login == "Copilot")] | last | "STATE=\(.state)"'
```

If the review state is `APPROVED` with no new inline comments, the loop is done — tell the user and stop.

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
4. Reply to each inline comment with the fix. DO NOT reply on the PR itself, only do inline comments:
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
- **Reply convention:** Always include the short commit hash and a brief description in reply bodies. Only reply inline, not on the PR.
- **Do not skip verification.** Always build (and test when appropriate) before pushing fixes.
- **Never defer unit tests.** If a reviewer requests unit tests, write them immediately in the same PR. Do not reply with "will add in a follow-up" or "acknowledged for later." Unit test requests are actionable comments that must be addressed with code, not acknowledgments.
- **Stop when approved** or when there are no actionable comments remaining.
