globalThis.athenaPage ??= (() => {
    let controls = [];

    const viewport = () => ({
        width: innerWidth, height: innerHeight, scrollX, scrollY, url: location.href
    });

    function observe() {
        const state = { ...viewport(), title: document.title, ready: !!document.body && document.readyState !== 'loading' };
        controls = [];
        const visible = [];
        const targets = 'a[href],button,input:not([type=hidden]),textarea,select,[role=button],[role=combobox],[contenteditable=true]';
        for (const element of document.querySelectorAll(targets)) {
            const rect = element.getBoundingClientRect();
            const left = Math.max(0, rect.left);
            const right = Math.min(innerWidth, rect.right);
            const top = Math.max(0, rect.top);
            const bottom = Math.min(innerHeight, rect.bottom);
            if (left >= right || top >= bottom || element.matches(':disabled,[aria-disabled=true],input[type=password]'))
                continue;
            const x = (left + right) / 2;
            const y = (top + bottom) / 2;
            if (!element.contains(document.elementFromPoint(x, y)))
                continue;
            const caption = element.localName === 'select' ? element.selectedOptions[0]?.text : element.innerText;
            const label = (element.getAttribute('aria-label') || element.labels?.[0]?.innerText ||
                element.getAttribute('placeholder') || caption || element.getAttribute('title') ||
                element.name || element.id || '').trim().replace(/\s+/g, ' ');
            if (!label)
                continue;
            const role = element.getAttribute('role') || (element.localName === 'input' ? element.type + ' field' : element.localName);
            controls.push(element);
            visible.push({ label: label.slice(0, 100), role, x, y });
            if (visible.length === 45)
                break;
        }

        const content = document.querySelector('main') || document.body;
        const snippets = [];
        let length = 0;
        for (const element of content?.querySelectorAll('h1,h2,h3,p,li,th,td') || []) {
            const rect = element.getBoundingClientRect();
            if (rect.width === 0 || rect.height === 0 || rect.bottom <= 0 || rect.top >= innerHeight)
                continue;
            const snippet = (element.innerText || '').trim().replace(/\s+/g, ' ').slice(0, 420);
            if (!snippet)
                continue;
            snippets.push(snippet);
            length += snippet.length;
            if (length >= 2300)
                break;
        }
        return { ...state, controls: visible, visibleText: snippets.join('\n').slice(0, 2300) };
    }

    function hasEditableFocus() {
        let active = document.activeElement;
        while (active?.shadowRoot?.activeElement)
            active = active.shadowRoot.activeElement;
        return !!(active?.isContentEditable || active?.matches(
            'textarea:not([readonly]),input:not([readonly]):is([type=text],[type=search],[type=email],[type=url],[type=tel],[type=number],:not([type]))'));
    }

    return {
        viewport, observe, hasEditableFocus,
        hits: (index, x, y) => index === null || !!controls[index - 1]?.contains(document.elementFromPoint(x, y))
    };
})();
