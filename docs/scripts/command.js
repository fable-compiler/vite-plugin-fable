import { LitElement, html, css } from "lit";
import copy from "copy-to-clipboard";

const COMMAND_STORAGE_KEY = "vpf_command";

class Command extends LitElement {
  static properties = {
    // {state: true} means Lit sees these class properties as internal.
    // Note that this is meta info and is not the same thing as this._value down below.
    _value: { state: true },
    _clicked: { state: true },
  };

  constructor() {
    super();
    /** @type String */
    this._value = "";
    /** @type Boolean */
    this._clicked = false;
    this.onValueChanged = this.onValueChanged.bind(this);
  }

  connectedCallback() {
    super.connectedCallback();
    window.addEventListener(COMMAND_STORAGE_KEY, this.onValueChanged);
  }

  disconnectedCallback() {
    super.disconnectedCallback();
    window.removeEventListener(COMMAND_STORAGE_KEY, this.onValueChanged);
  }

  static styles = css`
    div {
      background-color: var(--code-background);
      border-radius: var(--radius);
    }
    ul {
      display: flex;
      list-style: none;
      margin: 0;
      padding: 0 var(--spacing-200);
      gap: var(--spacing-400);
      border-bottom: 1px solid var(--header-border);
      cursor: pointer;
    }

    ul li {
      padding-block: var(--spacing-100);
      transition: 200ms all;
      border-bottom: 2px solid transparent;
    }

    ul li:hover {
      border-bottom: 2px solid var(--fable-400);
    }

    ul li.active {
      border-bottom: 2px solid var(--fable-600);
      font-weight: 500;
    }

    main {
      font-family: var(--monospace-font);
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--spacing-200);
      padding: var(--spacing-200);
    }

    main div {
      display: flex;
      align-items: center;
      gap: var(--spacing-200);
    }

    main > iconify-icon {
      cursor: pointer;
      transition: 200ms all;
    }

    main > iconify-icon:hover {
      color: var(--fable-700);
    }
  `;

  /**
   * @param {String} value
   */
  onOptionClick(value) {
    localStorage.setItem(COMMAND_STORAGE_KEY, value);
    const event = new CustomEvent(COMMAND_STORAGE_KEY, {
      detail: value,
    });
    window.dispatchEvent(event);
  }

  /** @param ev {CustomEvent<string>} */
  onValueChanged(ev) {
    this._value = ev.detail;
  }

  /** @param {String} contents */
  copyToClipboard(contents) {
    copy(contents);
    this._clicked = true;
    setTimeout(() => {
      this._clicked = false;
    }, 500);
  }

  render() {
    const attributes = [...this.attributes];

    if (attributes.length === 0) {
      return null;
    }

    const activeIndex = (() => {
      const stored = localStorage.getItem(COMMAND_STORAGE_KEY);
      if (!stored) {
        this._value = attributes[0].name;
        localStorage.setItem(COMMAND_STORAGE_KEY, this._value);
        return 0;
      } else {
        this.value = stored;
        return attributes.findIndex((a) => a.name === stored);
      }
    })();
    const command = attributes[activeIndex] && attributes[activeIndex].value;

    const copyElement = this._clicked
      ? html`<div>
          copied!
          <iconify-icon icon="system-uicons:clipboard-check" width="24" height="24"></iconify-icon>
        </div>`
      : html`<iconify-icon
          icon="system-uicons:clipboard"
          width="24"
          height="24"
          @click=${() => {
            this.copyToClipboard(command);
          }}
        ></iconify-icon>`;

    return html`
      <div>
        <ul>
          ${attributes.map((a, i) => {
            const className = i === activeIndex ? "active" : "";
            return html`<li class="${className}" @click=${() => this.onOptionClick(a.name)}>
              ${a.name}
            </li>`;
          })}
        </ul>
        <main>
          <div>
            <iconify-icon icon="tabler:prompt" width="24" height="24"></iconify-icon>
            ${command}
          </div>
          ${copyElement}
        </main>
      </div>
    `;
  }
}

customElements.define("vpf-command", Command);
