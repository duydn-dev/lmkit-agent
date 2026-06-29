(function () {
    // 1. Determine the base URL from the script tag itself
    var scripts = document.getElementsByTagName('script');
    var currentScript = scripts[scripts.length - 1]; // Assume the last loaded script is this one
    var baseUrl = 'http://localhost:5173'; // Fallback
    
    if (currentScript && currentScript.src) {
        var url = new URL(currentScript.src);
        baseUrl = url.origin;
    }

    // 2. Add styles for the widget components
    var style = document.createElement('style');
    style.innerHTML = `
        #lmkit-chat-widget-container {
            position: fixed;
            bottom: 24px;
            right: 24px;
            z-index: 2147483647; /* Max z-index to ensure it stays on top */
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
        }

        #lmkit-chat-button {
            width: 60px;
            height: 60px;
            border-radius: 50%;
            background-color: #2563eb; /* Tailwind blue-600 */
            box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
            cursor: pointer;
            display: flex;
            align-items: center;
            justify-content: center;
            transition: transform 0.2s ease, background-color 0.2s ease;
            position: absolute;
            bottom: 0;
            right: 0;
            border: none;
            outline: none;
        }

        #lmkit-chat-button:hover {
            transform: scale(1.05);
            background-color: #1d4ed8; /* Tailwind blue-700 */
        }

        #lmkit-chat-button svg {
            width: 32px;
            height: 32px;
            fill: white;
            transition: transform 0.3s ease;
        }

        #lmkit-chat-button.open svg.chat-icon {
            display: none;
        }

        #lmkit-chat-button.open svg.close-icon {
            display: block;
        }

        #lmkit-chat-button:not(.open) svg.chat-icon {
            display: block;
        }

        #lmkit-chat-button:not(.open) svg.close-icon {
            display: none;
        }

        #lmkit-chat-iframe-container {
            position: absolute;
            bottom: 80px;
            right: 0;
            width: 380px;
            height: 600px;
            max-height: calc(100vh - 120px);
            max-width: calc(100vw - 48px);
            background-color: transparent;
            border-radius: 16px;
            box-shadow: 0 12px 28px rgba(0, 0, 0, 0.15), 0 2px 4px rgba(0, 0, 0, 0.05);
            overflow: hidden;
            opacity: 0;
            transform: translateY(20px) scale(0.95);
            transform-origin: bottom right;
            transition: opacity 0.3s cubic-bezier(0.16, 1, 0.3, 1), transform 0.3s cubic-bezier(0.16, 1, 0.3, 1);
            pointer-events: none;
        }

        #lmkit-chat-iframe-container.open {
            opacity: 1;
            transform: translateY(0) scale(1);
            pointer-events: all;
        }

        #lmkit-chat-iframe {
            width: 100%;
            height: 100%;
            border: none;
            background-color: transparent;
        }

        @media (max-width: 480px) {
            #lmkit-chat-iframe-container {
                position: fixed;
                bottom: 0;
                right: 0;
                width: 100vw;
                height: 100vh;
                max-width: 100vw;
                max-height: 100vh;
                border-radius: 0;
            }
        }
    `;
    document.head.appendChild(style);

    // 3. Create the container
    var container = document.createElement('div');
    container.id = 'lmkit-chat-widget-container';
    document.body.appendChild(container);

    // 4. Create the Iframe Container
    var iframeContainer = document.createElement('div');
    iframeContainer.id = 'lmkit-chat-iframe-container';
    
    var iframe = document.createElement('iframe');
    iframe.id = 'lmkit-chat-iframe';
    // Append a query param to indicate it's embedded if needed
    iframe.src = baseUrl + '/widget/chat?embed=true';
    iframe.allow = "clipboard-write; clipboard-read"; // Allow permissions if needed
    
    iframeContainer.appendChild(iframe);
    container.appendChild(iframeContainer);

    // 5. Create the Button
    var button = document.createElement('button');
    button.id = 'lmkit-chat-button';
    button.innerHTML = `
        <svg class="chat-icon" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">
            <path d="M12 3c5.523 0 10 3.582 10 8s-4.477 8-10 8c-1.393 0-2.713-.243-3.896-.684L4 20l1.32-3.3C3.882 15.093 3 13.627 3 12c0-4.418 4.477-8 10-8zm0 2c-4.418 0-8 2.686-8 6 0 1.348.56 2.593 1.517 3.553l-1.045 2.613 3.655-.99C9.171 16.711 10.536 17 12 17c4.418 0 8-2.686 8-6s-3.582-6-8-6z"/>
        </svg>
        <svg class="close-icon" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">
            <path fill-rule="evenodd" clip-rule="evenodd" d="M18.364 5.636a2 2 0 010 2.828L14.828 12l3.536 3.536a2 2 0 11-2.828 2.828L12 14.828l-3.536 3.536a2 2 0 11-2.828-2.828L9.172 12 5.636 8.464a2 2 0 112.828-2.828L12 9.172l3.536-3.536a2 2 0 012.828 0z"/>
        </svg>
    `;
    container.appendChild(button);

    // 6. Toggle Logic
    var isOpen = false;
    button.addEventListener('click', function () {
        isOpen = !isOpen;
        if (isOpen) {
            iframeContainer.classList.add('open');
            button.classList.add('open');
            // Optional: send message to iframe to focus input
            iframe.contentWindow.postMessage({ type: 'lmkit-widget-open' }, '*');
        } else {
            iframeContainer.classList.remove('open');
            button.classList.remove('open');
            iframe.contentWindow.postMessage({ type: 'lmkit-widget-close' }, '*');
        }
    });

    // 7. Listen for messages from iframe (e.g. to close the widget from inside)
    window.addEventListener('message', function (event) {
        if (event.data && event.data.type === 'lmkit-close-widget') {
            isOpen = false;
            iframeContainer.classList.remove('open');
            button.classList.remove('open');
        }
    });

})();
