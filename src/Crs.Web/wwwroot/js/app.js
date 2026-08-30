// Carousel scroll functionality
window.scrollCarousel = (element, direction) => {
    if (!element) return;

    // Scroll by approximately one card width (350px + gap)
    const scrollAmount = 360 * direction;
    element.scrollBy({
        left: scrollAmount,
        behavior: 'smooth'
    });
};

window.downloadFile = (fileName, contentType, content) => {
    const blob = new Blob([content], { type: contentType });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.style.display = 'none';
    document.body.appendChild(anchor);
    anchor.click();
    document.body.removeChild(anchor);
    URL.revokeObjectURL(url);
};

window.hasFocusWithin = (element) => !!element && element.contains(document.activeElement);

window.theme = {
    apply: (isDarkMode) => {
        document.body.classList.toggle('dark-mode', isDarkMode);
        document.body.classList.toggle('light-mode', !isDarkMode);
    }
};

window.authForm = {
    readFields: (host) => {
        if (!host) {
            return { email: "", password: "" };
        }

        const email = host.querySelector("#home-email");
        const password = host.querySelector("#home-password");
        return {
            email: email && typeof email.value === "string" ? email.value : "",
            password: password && typeof password.value === "string" ? password.value : ""
        };
    },
    watchAutofill: (host) => {
        if (!host || host.dataset.autofillBound === "true") {
            return;
        }

        host.dataset.autofillBound = "true";
        host.addEventListener("animationstart", (event) => {
            if (event.animationName !== "onAutoFillStart") {
                return;
            }

            const input = event.target;
            if (!(input instanceof HTMLInputElement)) {
                return;
            }

            input.dispatchEvent(new Event("input", { bubbles: true }));
            input.dispatchEvent(new Event("change", { bubbles: true }));
        }, true);
    }
};
