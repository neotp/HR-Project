window.hrSearchableSelect = {
    read(root) {
        const select = root?.querySelector(':scope > select');
        if (!select) return null;

        const options = Array.from(select.options).map(option => ({
            value: option.value ?? '',
            text: (option.textContent ?? '').trim(),
            disabled: option.disabled,
            selected: option.selected
        }));
        const selected = select.selectedIndex >= 0 ? select.options[select.selectedIndex] : null;
        const placeholderOption = options.find(option => option.value === '');

        return {
            disabled: select.disabled,
            placeholder: placeholderOption?.text || '',
            selectedText: selected && selected.value !== '' ? (selected.textContent ?? '').trim() : '',
            options
        };
    },

    select(root, value) {
        const select = root?.querySelector(':scope > select');
        if (!select || select.disabled) return;
        select.value = value;
        select.dispatchEvent(new Event('change', { bubbles: true }));
    }
};
