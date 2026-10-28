// Local tests may reuse an existing installation through PLAYWRIGHT_MODULE.
// npm ci installs the pinned version for other machines and CI.
const runtime = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
module.exports = {
  chromium: {
    launch(options = {}) {
      return runtime.chromium.launch({
        ...options,
        channel: process.env.PLAYWRIGHT_CHANNEL || (process.platform === 'win32' ? 'chrome' : undefined)
      });
    }
  }
};
