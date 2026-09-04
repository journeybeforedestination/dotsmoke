// The chart's tabs, swapped in place rather than navigated to.
//
// An enhancement and nothing more: every tab is a real link to a real URL that renders the
// whole page, so a reader who never gets this file presses the same tabs and reads the same
// chart. Everything below only spares them the reload.
(() => {
    const app = document.querySelector(".app");
    const tabs = app?.querySelector(".tabs");
    const pane = app?.querySelector(".pane");

    // The contents of the access section, inside its <details> rather than around it:
    // replacing the <details> would reset `open` and collapse the log under a reader on
    // every tab press. Nothing would error; it would just feel broken.
    const log = app?.querySelector(".access .log");

    // Every page but the one the chart is on.
    if (!tabs || !pane) return;

    // The URL the tab already carries, asking its page for one part of itself. The page and
    // its parts are one handler apart, so there is no second address to keep in step.
    const partUrl = (href, handler) => {
        const url = new URL(href);
        url.searchParams.set("handler", handler);
        return url;
    };

    // Without this the section is right on arrival and quietly wrong afterwards: press a tab,
    // expand the log, and the search that press just caused is missing — the log contradicting
    // the exact thing it teaches. A failure here leaves the rows a reader already has rather
    // than navigating away from a pane that just arrived; the next press asks again.
    const refreshLog = async (href) => {
        if (!log) return;

        try {
            const response = await fetch(partUrl(href, "access"));
            if (response.ok) log.innerHTML = await response.text();
        } catch {
            // Nothing to do that is better than leaving the log as it was.
        }
    };

    const show = async (href) => {
        let response;

        try {
            response = await fetch(partUrl(href, "pane"));
        } catch {
            // Offline, or the app went away mid-read. The navigation says so properly.
            location.href = href;
            return;
        }

        // The launch expired, or names a patient this URL disagrees with. Handing it to the
        // browser lands on /error with a sentence; showing the refusal in the pane would
        // leave the banner above it still claiming a patient whose launch is gone.
        if (!response.ok) {
            location.href = href;
            return;
        }

        pane.innerHTML = await response.text();

        // After the pane, never alongside it: reading a panel is itself a logged request,
        // and a section fetched in parallel would race the row it exists to show.
        await refreshLog(href);

        for (const tab of tabs.querySelectorAll("a")) {
            if (tab.href === href) tab.setAttribute("aria-current", "page");
            else tab.removeAttribute("aria-current");
        }
    };

    tabs.addEventListener("click", (event) => {
        // A middle click, a modified click or a right click is the reader asking for a
        // new tab or a copied address. That belongs to the browser.
        if (
            event.defaultPrevented ||
            event.button !== 0 ||
            event.metaKey ||
            event.ctrlKey ||
            event.shiftKey ||
            event.altKey
        )
            return;

        const tab = event.target.closest("a");
        if (!tab) return;

        event.preventDefault();

        // Pushed rather than replaced, and pushed with the tab's own href: the URL a
        // reader copies out of the bar has to be the one that renders what they are
        // looking at, because that URL outlives this page.
        history.pushState(null, "", tab.href);
        show(tab.href);
    });

    // The same fetch serves the back button: a URL naming no panel renders the same "pick a
    // panel" note it renders on arrival, so going back past the first tab needs no case.
    addEventListener("popstate", () => show(location.href));
})();
