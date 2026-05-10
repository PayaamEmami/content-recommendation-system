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

window.userMenu = {
    hasFocusWithin: (element) => !!element && element.contains(document.activeElement)
};

window.theme = {
    apply: (isDarkMode) => {
        document.body.classList.toggle('dark-mode', isDarkMode);
        document.body.classList.toggle('light-mode', !isDarkMode);
    }
};
