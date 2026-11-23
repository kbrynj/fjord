if (new URLSearchParams(location.search).get('dark')) document.documentElement.classList.add('dark');
  document.querySelectorAll('pre').forEach(pre => {
    const b = document.createElement('button');
    b.textContent = 'Copy';
    b.onclick = () => { navigator.clipboard.writeText(pre.querySelector('code').innerText); b.textContent = 'Copied'; setTimeout(() => b.textContent = 'Copy', 1200); };
    pre.appendChild(b);
  });
