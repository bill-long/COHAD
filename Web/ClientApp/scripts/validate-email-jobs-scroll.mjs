#!/usr/bin/env node
/**
 * Measures window scroll after: mock login → send-email → job detail → Back to Email Jobs.
 * Fails if scrollY stays near 0 (compose form still at top of viewport).
 *
 * Usage (MockData API + mock Angular, same origin):
 *   BASE_URL=https://127.0.0.1:5001 node scripts/validate-email-jobs-scroll.mjs
 *
 * Optional:
 *   JOB_ID=33333333-3333-3333-3333-333333333308  (default: oldest mock seed row)
 *   PUPPETEER_EXECUTABLE_PATH=/path/to/chrome  (optional; otherwise uses system Chrome/Chromium)
 */
import { execSync } from 'node:child_process';
import fs from 'node:fs';
import puppeteer from 'puppeteer';

function resolveChromeExecutable() {
  const fromEnv = process.env.PUPPETEER_EXECUTABLE_PATH || process.env.CHROME_BIN;
  if (fromEnv && fs.existsSync(fromEnv)) {
    return fromEnv;
  }
  const candidates = [
    '/usr/bin/google-chrome-stable',
    '/usr/bin/google-chrome',
    '/usr/bin/chromium',
    '/usr/bin/chromium-browser',
    '/snap/bin/chromium',
  ];
  for (const p of candidates) {
    if (fs.existsSync(p)) {
      return p;
    }
  }
  try {
    const out = execSync(
      'command -v google-chrome-stable || command -v google-chrome || command -v chromium || command -v chromium-browser',
      { encoding: 'utf8', shell: '/bin/sh' }
    );
    const first = out.trim().split('\n')[0]?.trim();
    if (first && fs.existsSync(first)) {
      return first;
    }
  } catch {
    // ignore
  }
  throw new Error(
    'No Chrome/Chromium found for Puppeteer. Install google-chrome-stable/chromium or set PUPPETEER_EXECUTABLE_PATH.'
  );
}

const baseUrl = (process.env.BASE_URL || 'https://127.0.0.1:5001').replace(/\/$/, '');
const jobId = (process.env.JOB_ID || '33333333-3333-3333-3333-333333333308').toLowerCase();

const minScrollY = Number(process.env.MIN_SCROLL_Y || '120');

async function main() {
  const browser = await puppeteer.launch({
    executablePath: resolveChromeExecutable(),
    headless: 'new',
    args: ['--no-sandbox', '--disable-setuid-sandbox', '--ignore-certificate-errors'],
    ignoreHTTPSErrors: true,
  });

  const page = await browser.newPage();
  await page.setViewport({ width: 1280, height: 720 });

  page.on('console', msg => {
    if (msg.type() === 'error') {
      console.error('browser console:', msg.text());
    }
  });

  try {
    await page.goto(baseUrl, { waitUntil: 'load', timeout: 60000 });
    await new Promise(r => setTimeout(r, 1500));

    // Mock login: open "Sign In As..." and choose admin user
    const openedMenu = await page.evaluate(() => {
      const btn = [...document.querySelectorAll('button')].find(b =>
        (b.textContent || '').includes('Sign In As'));
      if (!btn) {
        return false;
      }
      btn.click();
      return true;
    });
    if (!openedMenu) {
      throw new Error('Could not find "Sign In As" button (is this mock build + MockData API?)');
    }

    await page.waitForSelector('.mat-mdc-menu-panel button, .mat-menu-panel button', { timeout: 10000 });
    await new Promise(r => setTimeout(r, 300));

    const clickedUser = await page.evaluate(() => {
      const item = [...document.querySelectorAll('.mat-mdc-menu-panel button, .mat-menu-panel button, button')].find(
        b => (b.textContent || '').includes('Mock Resident (Admin)')
      );
      if (item) {
        item.click();
        return true;
      }
      return false;
    });
    if (!clickedUser) {
      throw new Error('Could not click mock admin user in menu');
    }

    // Wait for JWT + api/me (navbar shows email). Do NOT page.goto next — full reload drops in-memory mock token.
    await page.waitForFunction(
      () => document.body.innerText.includes('mock@cohad.local'),
      { timeout: 40000 }
    );
    await new Promise(r => setTimeout(r, 1500));

    const clickedManage = await page.evaluate(() => {
      const a = [...document.querySelectorAll('a')].find(el => (el.textContent || '').trim() === 'Manage');
      if (a) {
        a.click();
        return true;
      }
      return false;
    });
    if (!clickedManage) {
      throw new Error('Could not find Manage nav link');
    }

    await page.waitForFunction(() => window.location.pathname.startsWith('/manage'), { timeout: 15000 });
    await new Promise(r => setTimeout(r, 500));

    const clickedEmail = await page.evaluate(() => {
      const a = [...document.querySelectorAll('a')].find(
        el => (el.textContent || '').trim() === 'Email' && el.getAttribute('href')?.includes('send-email')
      );
      if (a) {
        a.click();
        return true;
      }
      return false;
    });
    if (!clickedEmail) {
      throw new Error('Could not find Email tab under Manage');
    }

    await page.waitForFunction(() => window.location.pathname.includes('send-email'), { timeout: 15000 });
    await new Promise(r => setTimeout(r, 2000));

    await page.waitForSelector('#email-jobs', { timeout: 25000 });
    await page.waitForSelector('table.jobs-table tbody tr', { timeout: 20000 });

    const beforeBack = await page.evaluate(() => ({
      scrollY: window.scrollY,
      emailJobsTop: document.getElementById('email-jobs')?.getBoundingClientRect().top ?? null,
      composeTitleTop: document.querySelector('.send-email-page h2.page-title')?.getBoundingClientRect().top ?? null,
    }));

    const openedJob = await page.evaluate(id => {
      const row = document.getElementById(`email-job-row-${id}`);
      if (row) {
        row.click();
        return true;
      }
      return false;
    }, jobId);
    if (!openedJob) {
      throw new Error(`Could not find job row email-job-row-${jobId}`);
    }

    await page.waitForFunction(() => window.location.pathname.includes('/email-jobs/'), { timeout: 15000 });
    await new Promise(r => setTimeout(r, 1500));
    await page.waitForSelector('button.back-link', { timeout: 15000 });
    await page.click('button.back-link');

    await page.waitForFunction(() => window.location.pathname.includes('/manage/send-email'), {
      timeout: 15000,
    });

    // Router may replace the URL to drop focusJob/#email-jobs after scrolling; allow scroll + strip to finish.
    await new Promise(r => setTimeout(r, 1200));

    const afterBack = await page.evaluate(id => {
      const jobsEl = document.getElementById('email-jobs');
      const composeTitle = document.querySelector('.send-email-page h2.page-title');
      const row = document.getElementById(`email-job-row-${id}`);
      return {
        href: window.location.href,
        scrollY: window.scrollY,
        scrollMax: Math.max(0, document.documentElement.scrollHeight - window.innerHeight),
        emailJobsTop: jobsEl ? jobsEl.getBoundingClientRect().top : null,
        composeTitleTop: composeTitle ? composeTitle.getBoundingClientRect().top : null,
        rowTop: row ? row.getBoundingClientRect().top : null,
      };
    }, jobId);

    console.log(JSON.stringify({ baseUrl, jobId, beforeBack, afterBack }, null, 2));

    const scrollY = afterBack.scrollY;
    const jobsTop = afterBack.emailJobsTop;
    const titleTop = afterBack.composeTitleTop;

    // Requirement: compose heading is NOT pinned at top of viewport; jobs block is in view.
    const jobsBlockNearViewportTop =
      jobsTop != null && jobsTop < 200 && jobsTop > -100;
    const pageScrolledDown = scrollY >= minScrollY;
    const composeHeadingScrolledUp = titleTop != null && titleTop < -10;

    const ok = pageScrolledDown || composeHeadingScrolledUp || jobsBlockNearViewportTop;

    if (!ok) {
      throw new Error(
        `Scroll check failed: scrollY=${scrollY} (want >=${minScrollY} or title above viewport or jobs near top). ` +
          `composeTitleTop=${titleTop}, emailJobsTop=${jobsTop}, href=${afterBack.href}`
      );
    }

    console.log(
      'OK: scrollY=%d composeTitleTop=%s emailJobsTop=%s rowTop=%s',
      scrollY,
      titleTop,
      jobsTop,
      afterBack.rowTop
    );
  } finally {
    await browser.close();
  }
}

main().catch(err => {
  console.error(err);
  process.exit(1);
});
