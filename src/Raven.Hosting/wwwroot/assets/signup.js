// "Create a free identity" goes to the sign-up host of the visitor's region: each region is its own cluster with its
// own sign-up host, so picking the host is picking the region. The page carries the EU host (SitePages.DefaultSignupUrl);
// this swaps in the visitor's. Keep the rules in step with detectRegion in odin-js
// (provisioning-app/src/helpers/region.ts), or every visitor the two disagree on pays a redirect.
(() => {
  const urls = { eu: "https://createme.eu.ravenhosting.cloud/", na: "https://createme.na.ravenhosting.cloud/" };
  const eu = /^(DE|FR|GB|IE|NL|BE|LU|AT|CH|IT|ES|PT|PL|CZ|SK|HU|RO|BG|GR|DK|SE|NO|FI|IS|EE|LV|LT|SI|HR|MT|CY|UA|RS|BA|AL|MK|ZA|NG|KE|EG|MA|IL|TR|SA|AE)$/;
  const na = /^(CA|US|MX|BR|AR|CL|CO|PE|VE|EC|BO|PY|UY|CR|PA|GT|CU|DO|JM)$/;

  // The time zone first: Chrome and Edge default the language list to en-US whatever the machine's locale
  const region = () => {
    try {
      const zone = Intl.DateTimeFormat().resolvedOptions().timeZone || "";
      if (/^(Europe|Africa|Atlantic)\//.test(zone)) return "eu";
      if (/^America\//.test(zone)) return "na";
    } catch {
      // No Intl; try the languages
    }
    for (const language of navigator.languages?.length ? navigator.languages : [navigator.language]) {
      try {
        const country = new Intl.Locale(language).region;
        if (country && eu.test(country)) return "eu";
        if (country && na.test(country)) return "na";
      } catch {
        // A malformed tag; try the next
      }
    }
    return null;
  };

  const url = urls[region()];
  if (url) {
    document.querySelectorAll("a[data-signup]").forEach((a) => (a.href = url));
  }
})();
