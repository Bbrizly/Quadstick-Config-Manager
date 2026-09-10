(() => {
  const mount = document.getElementById('clinic-console');
  if (!mount) return;

  const style = document.createElement('style');
  style.textContent = `
    #clinic-console{--qcd-bg:#f6f8f7;--qcd-card:#fff;--qcd-ink:#17211e;--qcd-muted:#6f7c77;--qcd-faint:#98a29e;--qcd-line:#dde5e1;--qcd-green:#1f4e46;--qcd-green2:#2d6f60;--qcd-soft:#e5f0eb;--qcd-blue:#2f6d9d;--qcd-blueSoft:#e7f0f7;--qcd-gold:#9a7022;--qcd-goldSoft:#f8edd3;--qcd-rose:#9d5368;--qcd-roseSoft:#f7e7ec;--qcd-shadow:0 18px 45px -30px rgba(22,45,38,.28)}
    #clinic-console *{box-sizing:border-box}
    #clinic-console button,#clinic-console input,#clinic-console textarea,#clinic-console select{font:inherit}
    #clinic-console button{cursor:pointer}
    #clinic-console .screen{background:var(--qcd-bg);overflow:hidden}
    #clinic-console .qcd-app{height:690px;min-height:620px;display:grid;grid-template-columns:178px minmax(0,1fr);background:var(--qcd-bg);color:var(--qcd-ink);font-family:"Instrument Sans",system-ui,sans-serif}
    #clinic-console .qcd-side{display:flex;flex-direction:column;padding:13px 10px;background:#142824;color:#d7e7e1}
    #clinic-console .qcd-brand{display:flex;align-items:center;gap:9px;width:100%;padding:7px 8px 17px;border:0;background:transparent;color:#fff;text-align:left;font-family:"Space Grotesk",sans-serif;font-size:13px;font-weight:650;border-radius:10px}
    #clinic-console .qcd-brand:hover{background:rgba(255,255,255,.06)}
    #clinic-console .qcd-brand img{width:25px;height:25px;filter:brightness(0) invert(1);opacity:.94}
    #clinic-console .qcd-nav{display:grid;gap:4px}
    #clinic-console .qcd-nav button{display:flex;align-items:center;gap:9px;width:100%;padding:9px 10px;border:0;border-radius:9px;background:transparent;color:#b8cdc6;text-align:left;font-size:11px;font-weight:550}
    #clinic-console .qcd-nav button:hover,#clinic-console .qcd-nav button.active{background:#24453d;color:#fff}
    #clinic-console .qcd-nav svg{width:15px;height:15px;flex:none}
    #clinic-console .qcd-side-foot{margin-top:auto;padding:13px 7px 4px;border-top:1px solid rgba(255,255,255,.09)}
    #clinic-console .qcd-clinician{display:flex;align-items:center;gap:8px}.qcd-doc-avatar{width:30px;height:30px;display:grid;place-items:center;border-radius:9px;background:#dce9e2;color:#1f4e46;font:700 10px "Space Grotesk",sans-serif}
    #clinic-console .qcd-clinician b{display:block;font-size:10px}#clinic-console .qcd-clinician span{display:block;margin-top:1px;color:#8ba9a0;font-size:8px}
    #clinic-console .qcd-main{min-width:0;display:flex;flex-direction:column;overflow:hidden}
    #clinic-console .qcd-top{height:52px;flex:none;display:flex;align-items:center;gap:9px;padding:8px 13px;border-bottom:1px solid var(--qcd-line);background:#fff}
    #clinic-console .qcd-back{display:none;align-items:center;gap:6px;padding:7px 9px;border:1px solid var(--qcd-line);border-radius:8px;background:#fff;color:#52635d;font-size:10px;font-weight:650}
    #clinic-console .qcd-back.visible{display:flex}#clinic-console .qcd-back:hover{background:#f3f6f4;color:#1f4e46}
    #clinic-console .qcd-back svg{width:13px;height:13px}
    #clinic-console .qcd-search{position:relative;flex:1;max-width:510px}#clinic-console .qcd-search input{width:100%;height:34px;padding:0 11px 0 33px;border:1px solid #d8e0dc;border-radius:9px;background:#f8faf9;outline:none;font-size:10px;color:#26352f}
    #clinic-console .qcd-search input:focus{border-color:#81a79c;box-shadow:0 0 0 3px rgba(45,111,96,.09)}#clinic-console .qcd-search svg{position:absolute;left:11px;top:10px;width:13px;height:13px;color:#8a9893}
    #clinic-console .qcd-current{margin-left:auto;display:flex;align-items:center;gap:7px;color:#70807a;font-size:9px}.qcd-current strong{color:#2e4039;font-size:9px}
    #clinic-console .qcd-notice{height:0;overflow:hidden;flex:none;background:#edf5f1;color:#2d6255;border-bottom:0 solid #d5e7df;font-size:10px;transition:height .24s ease,padding .24s ease,border-width .24s ease;padding:0 15px}
    #clinic-console .qcd-notice.show{height:34px;padding:8px 15px;border-bottom-width:1px}
    #clinic-console .qcd-content{flex:1;min-height:0;overflow:auto;padding:17px}
    #clinic-console .qcd-page-head{display:flex;justify-content:space-between;align-items:flex-start;gap:14px;margin-bottom:14px}#clinic-console .qcd-page-head h3{margin:0;font:700 23px "Space Grotesk",sans-serif;letter-spacing:-.03em}#clinic-console .qcd-page-head p{margin:3px 0 0;color:var(--qcd-muted);font-size:10px}
    #clinic-console .qcd-actions{display:flex;gap:7px;flex-wrap:wrap}.qcd-btn{display:inline-flex;align-items:center;justify-content:center;gap:6px;padding:7px 10px;border:1px solid #d4ded9;border-radius:8px;background:#fff;color:#2c3d37;font-size:9.5px;font-weight:650}.qcd-btn:hover{background:#f2f6f4}.qcd-btn.primary{background:#286656;border-color:#286656;color:#fff}.qcd-btn.soft{background:#eef5f2;border-color:#d8e7e1;color:#2d6759}.qcd-btn.blue{background:#2f6d9d;border-color:#2f6d9d;color:#fff}
    #clinic-console .qcd-panel{background:#fff;border:1px solid var(--qcd-line);border-radius:12px;box-shadow:0 1px 1px rgba(21,37,32,.02)}
    #clinic-console .qcd-home-grid{display:grid;grid-template-columns:minmax(0,1.7fr) minmax(220px,.68fr);gap:12px}
    #clinic-console .qcd-toolbar{display:flex;align-items:center;gap:7px;padding:9px 10px;border-bottom:1px solid #e7ece9}.qcd-toolbar input,.qcd-toolbar select{height:30px;border:1px solid #dbe3df;border-radius:7px;background:#fff;padding:0 9px;font-size:9px;color:#42534d}.qcd-toolbar input{flex:1;min-width:90px}.qcd-toolbar .grow{flex:1}
    #clinic-console .qcd-table{width:100%;border-collapse:collapse}#clinic-console .qcd-table th{padding:8px 10px;background:#fafbfa;border-bottom:1px solid #e6ebe8;color:#8c9894;text-align:left;font-size:8px;font-weight:650;letter-spacing:.01em}#clinic-console .qcd-table td{padding:9px 10px;border-bottom:1px solid #edf1ef;font-size:9px;vertical-align:middle}#clinic-console .qcd-table tr:last-child td{border-bottom:0}#clinic-console .qcd-table tbody tr{cursor:pointer;transition:background .15s ease}#clinic-console .qcd-table tbody tr:hover{background:#f5f8f6}
    #clinic-console .qcd-person{display:flex;align-items:center;gap:8px;min-width:0}.qcd-avatar{width:31px;height:31px;border-radius:10px;object-fit:cover;flex:none;background:#e8efec}.qcd-avatar.lg{width:50px;height:50px;border-radius:14px}.qcd-avatar.xl{width:58px;height:58px;border-radius:16px}.qcd-person b{display:block;font-size:9.5px}.qcd-person small{display:block;color:#8e9995;font-size:7.8px;margin-top:1px}.qcd-id{font:500 8px "JetBrains Mono",monospace;color:#93a09b}
    #clinic-console .qcd-status,.qcd-tag,.qcd-match,.qcd-outcome{display:inline-flex;align-items:center;border-radius:999px;white-space:nowrap;font-weight:650}.qcd-status{padding:3px 7px;background:#ddf1e7;color:#236e50;font-size:7.8px}.qcd-tag{padding:3px 6px;background:#e8f0f6;color:#3b6685;font-size:7.7px}.qcd-tag.gold{background:#f8edd3;color:#86651d}.qcd-tag.rose{background:#f7e7ec;color:#925066}.qcd-match{padding:3px 7px;background:#fff0ce;color:#87611b;font-size:8px}.qcd-outcome{padding:3px 7px;font-size:8px}.qcd-outcome.good{background:#def2e8;color:#216f50}.qcd-outcome.same{background:#edf0ef;color:#5d6864}.qcd-outcome.mixed{background:#fff0d9;color:#8d611b}
    #clinic-console .qcd-recent{padding:11px}.qcd-recent h4{margin:0 0 8px;font:700 10px "Space Grotesk",sans-serif}.qcd-recent-item{display:flex;align-items:center;gap:8px;padding:8px 0;border-bottom:1px solid #edf1ef}.qcd-recent-item:last-child{border-bottom:0}.qcd-recent-copy{min-width:0;flex:1}.qcd-recent-copy b{display:block;font-size:8.8px}.qcd-recent-copy span{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:#87948f;font-size:7.8px}.qcd-recent-item time{color:#99a29e;font-size:7px}
    #clinic-console .qcd-client-hero{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:12px 13px;margin-bottom:10px;background:linear-gradient(115deg,#fff 65%,#f1f7f4);border:1px solid var(--qcd-line);border-radius:13px}.qcd-client-left{display:flex;align-items:center;gap:11px}.qcd-client-copy h3{margin:0;font:700 19px "Space Grotesk",sans-serif}.qcd-client-copy .meta{display:flex;align-items:center;gap:7px;flex-wrap:wrap;margin-top:3px;color:#697872;font-size:8.7px}.qcd-client-copy .goal{margin-top:6px;color:#31453e;font-size:9px}.qcd-mini-stats{display:flex;gap:16px}.qcd-mini-stats div{text-align:right}.qcd-mini-stats b{display:block;font-size:9px}.qcd-mini-stats span{display:block;color:#929d99;font-size:7.5px}
    #clinic-console .qcd-tabs{display:flex;gap:3px;margin:0 1px 11px;border-bottom:1px solid #dfe6e2}.qcd-tabs button{padding:7px 9px;border:0;border-bottom:2px solid transparent;background:transparent;color:#7c8984;font-size:8.8px;font-weight:650}.qcd-tabs button.active{border-color:#2d6f60;color:#245e51}
    #clinic-console .qcd-workspace{display:grid;grid-template-columns:minmax(0,1fr) 215px;gap:11px}.qcd-timeline{position:relative;padding:4px 0 5px}.qcd-timeline:before{content:"";position:absolute;left:64px;top:12px;bottom:14px;width:1px;background:#dbe5e0}.qcd-event{position:relative;display:grid;grid-template-columns:51px 1fr;gap:24px;margin-bottom:10px}.qcd-event-date{text-align:right;padding-top:9px;color:#89958f;font:500 7.7px "JetBrains Mono",monospace}.qcd-event-dot{position:absolute;left:60px;top:13px;width:9px;height:9px;border:2px solid #fff;border-radius:50%;background:#4e8a79;box-shadow:0 0 0 1px #bcd5cc}.qcd-session-card{padding:11px 12px;border:1px solid #dfe6e2;border-radius:11px;background:#fff;box-shadow:0 10px 26px -24px rgba(20,42,35,.5)}.qcd-session-top{display:flex;justify-content:space-between;gap:8px}.qcd-session-title b{font-size:9.5px}.qcd-session-title span{display:block;margin-top:1px;color:#89958f;font-size:7.8px}.qcd-session-time{color:#9aa39f;font-size:7px}.qcd-quote{margin:8px 0 0;padding:7px 9px;border-left:2px solid #b8d2c8;background:#f5f8f6;color:#53645e;font-size:8.4px;line-height:1.4}.qcd-change-list{display:flex;flex-wrap:wrap;gap:4px;margin-top:8px}.qcd-change{padding:3px 6px;border-radius:6px;background:#f0f4f2;color:#596963;font-size:7.5px}.qcd-change strong{color:#295d50}.qcd-session-foot{display:flex;align-items:center;justify-content:space-between;gap:8px;margin-top:9px}.qcd-session-actions{display:flex;gap:5px}
    #clinic-console .qcd-similar{display:grid;gap:8px}.qcd-similar-head{display:flex;align-items:center;justify-content:space-between;margin-bottom:1px}.qcd-similar-head b{font:700 10px "Space Grotesk",sans-serif}.qcd-link{padding:0;border:0;background:transparent;color:#2d6f60;font-size:8px;font-weight:650}.qcd-case-card{padding:10px;border:1px solid #dfe6e2;border-radius:11px;background:#fff}.qcd-case-top{display:flex;justify-content:space-between;align-items:flex-start;gap:7px}.qcd-case-person{display:flex;gap:7px;align-items:center}.qcd-case-person .qcd-avatar{width:28px;height:28px;border-radius:9px}.qcd-case-person b{display:block;font-size:8.8px}.qcd-case-person span{display:block;color:#89958f;font-size:7.4px}.qcd-why{margin-top:7px}.qcd-why b{display:block;font-size:7.7px}.qcd-why ul{display:grid;gap:2px;margin:4px 0 0;padding:0;list-style:none;color:#6b7974;font-size:7.4px}.qcd-why li:before{content:"✓";margin-right:5px;color:#2d8067}.qcd-case-outcome{margin-top:7px}.qcd-case-actions{display:flex;gap:5px;margin-top:8px}
    #clinic-console .qcd-search-page{display:grid;gap:10px}.qcd-case-searchbar{display:flex;gap:7px;padding:10px;border:1px solid var(--qcd-line);border-radius:11px;background:#fff}.qcd-case-searchbar input{flex:1;height:32px;border:1px solid #dce4e0;border-radius:8px;padding:0 10px;font-size:9px;outline:none}.qcd-filter-row{display:flex;gap:5px;flex-wrap:wrap}.qcd-filter{padding:5px 7px;border:1px solid #dbe4df;border-radius:7px;background:#fff;color:#54655f;font-size:8px}.qcd-result{padding:11px 12px;border:1px solid var(--qcd-line);border-radius:11px;background:#fff}.qcd-result-head{display:flex;justify-content:space-between;align-items:flex-start;gap:10px}.qcd-result-grid{display:grid;grid-template-columns:1fr 1.1fr .82fr;gap:12px;margin-top:9px;padding-top:9px;border-top:1px solid #edf0ef}.qcd-result-grid h5{margin:0 0 5px;font-size:7.8px;color:#63736d}.qcd-result-grid ul{display:grid;gap:3px;margin:0;padding:0;list-style:none;color:#65756f;font-size:7.6px}.qcd-result-grid li:before{content:"✓";margin-right:5px;color:#2b7d65}.qcd-result-grid p{margin:0;color:#62726c;font-size:7.7px;line-height:1.45}.qcd-result-actions{display:flex;justify-content:flex-end;gap:5px;margin-top:8px}
    #clinic-console .qcd-compare-head{display:flex;align-items:center;justify-content:space-between;gap:10px;margin-bottom:11px}.qcd-compare-title h3{margin:0;font:700 21px "Space Grotesk",sans-serif}.qcd-compare-title p{margin:3px 0 0;color:#77857f;font-size:9px}.qcd-compare-people{display:grid;grid-template-columns:1fr 36px 1fr;gap:9px;align-items:center;margin-bottom:10px}.qcd-compare-person{display:flex;align-items:center;gap:9px;padding:10px 11px;border:1px solid var(--qcd-line);border-radius:11px;background:#fff}.qcd-compare-person b{display:block;font-size:9.5px}.qcd-compare-person span{display:block;color:#84918c;font-size:7.7px}.qcd-vs{display:grid;place-items:center;width:28px;height:28px;margin:auto;border-radius:50%;background:#edf4f1;color:#44685d;font:700 8px "Space Grotesk",sans-serif}.qcd-diff-table{overflow:hidden;border:1px solid var(--qcd-line);border-radius:11px;background:#fff}.qcd-diff-row{display:grid;grid-template-columns:1.2fr .8fr .8fr .8fr;align-items:center;min-height:34px;border-bottom:1px solid #edf1ef}.qcd-diff-row:last-child{border-bottom:0}.qcd-diff-row.header{min-height:30px;background:#f8faf9;color:#89958f;font-size:7.5px;font-weight:650}.qcd-diff-row>div{padding:7px 9px;font-size:8.2px}.qcd-diff-label b{display:block;font-size:8.4px}.qcd-diff-label span{display:block;margin-top:1px;color:#919c98;font-size:7px}.qcd-value{font:600 8px "JetBrains Mono",monospace}.qcd-delta{font:650 7.8px "JetBrains Mono",monospace}.qcd-delta.up{color:#a45142}.qcd-delta.down{color:#31745f}.qcd-delta.same{color:#8a9591}.qcd-bars{display:flex;gap:3px;align-items:center;margin-top:4px}.qcd-bar{height:3px;border-radius:999px;background:#dce5e1;overflow:hidden;flex:1}.qcd-bar i{display:block;height:100%;background:#5b8f80;border-radius:999px}.qcd-compare-note{display:grid;grid-template-columns:1fr 1fr;gap:9px;margin-top:10px}.qcd-note-card{padding:10px;border:1px solid var(--qcd-line);border-radius:10px;background:#fff}.qcd-note-card b{display:block;font-size:8.5px}.qcd-note-card p{margin:4px 0 0;color:#65756f;font-size:8px;line-height:1.45}
    #clinic-console .qcd-form-grid{display:grid;grid-template-columns:minmax(0,1fr) 190px;gap:11px}.qcd-session-form{padding:13px;border:1px solid var(--qcd-line);border-radius:11px;background:#fff}.qcd-field{margin-bottom:11px}.qcd-field label{display:block;margin-bottom:5px;color:#53645e;font-size:8px;font-weight:650}.qcd-field textarea,.qcd-field input,.qcd-field select{width:100%;border:1px solid #dbe4df;border-radius:8px;background:#fff;padding:8px 9px;color:#31423c;font-size:8.5px;outline:none}.qcd-field textarea{min-height:64px;resize:none}.qcd-focuses{display:flex;gap:4px;flex-wrap:wrap}.qcd-focus{padding:5px 8px;border:1px solid #dbe4df;border-radius:7px;background:#fff;color:#687872;font-size:8px}.qcd-focus.active{background:#e8f2ee;border-color:#bad4c9;color:#2b6658}.qcd-change-grid{display:grid;grid-template-columns:1.3fr .65fr 18px .65fr;gap:5px;align-items:center}.qcd-change-grid+ .qcd-change-grid{margin-top:5px}.qcd-change-grid span{font-size:7.8px;color:#61716b}.qcd-change-grid input{height:28px;padding:0 7px}.qcd-arrow{text-align:center;color:#a0aaa6}.qcd-outcome-panel{padding:12px;border:1px solid var(--qcd-line);border-radius:11px;background:#fff}.qcd-outcome-panel h4{margin:0 0 8px;font:700 10px "Space Grotesk",sans-serif}.qcd-outcome-option{display:flex;align-items:center;gap:7px;width:100%;padding:8px;border:1px solid #e0e6e3;border-radius:8px;background:#fff;color:#52635d;text-align:left;font-size:8.5px;margin-bottom:5px}.qcd-outcome-option.active{background:#e6f2ed;border-color:#b9d7cb;color:#246451}.qcd-dot{width:8px;height:8px;border:2px solid currentColor;border-radius:50%}
    #clinic-console .qcd-empty{padding:30px;text-align:center;color:#7b8984;font-size:9px}.qcd-profile-list{display:grid;gap:8px}.qcd-profile-row{display:grid;grid-template-columns:38px 1fr auto;align-items:center;gap:9px;padding:10px;border:1px solid var(--qcd-line);border-radius:10px;background:#fff}.qcd-profile-icon{display:grid;place-items:center;width:34px;height:34px;border-radius:9px;background:#e8f1ed;color:#2d6f60;font-size:15px}.qcd-profile-row b{display:block;font-size:9px}.qcd-profile-row span{display:block;margin-top:2px;color:#87948f;font-size:7.6px}.qcd-detail-grid{display:grid;grid-template-columns:1fr 1fr;gap:8px}.qcd-detail-card{padding:11px;border:1px solid var(--qcd-line);border-radius:10px;background:#fff}.qcd-detail-card b{display:block;font-size:8px;color:#86928d}.qcd-detail-card strong{display:block;margin-top:3px;font-size:9.5px}.qcd-detail-card p{margin:4px 0 0;color:#66756f;font-size:8px;line-height:1.4}
    @media(max-width:1050px){#clinic-console .qcd-app{grid-template-columns:158px minmax(0,1fr)}#clinic-console .qcd-workspace{grid-template-columns:1fr}.qcd-similar{display:none}#clinic-console .qcd-home-grid{grid-template-columns:1fr}.qcd-home-grid>.qcd-panel:last-child{display:none}}
    @media(max-width:760px){#clinic-console .qcd-app{height:auto;min-height:680px;grid-template-columns:1fr}#clinic-console .qcd-side{display:none}#clinic-console .qcd-top{position:sticky;top:0;z-index:3}#clinic-console .qcd-current{display:none}#clinic-console .qcd-content{padding:12px}#clinic-console .qcd-page-head{flex-direction:column}.qcd-client-hero{align-items:flex-start!important}.qcd-mini-stats{display:none}.qcd-timeline:before{left:43px}.qcd-event{grid-template-columns:34px 1fr;gap:20px}.qcd-event-dot{left:39px}.qcd-result-grid{grid-template-columns:1fr!important}.qcd-compare-people{grid-template-columns:1fr}.qcd-vs{display:none}.qcd-diff-row{grid-template-columns:1.2fr .7fr .7fr}.qcd-diff-row>div:last-child{display:none}.qcd-form-grid{grid-template-columns:1fr}.qcd-detail-grid{grid-template-columns:1fr}}
  `;
  document.head.appendChild(style);

  const icon = (name) => {
    const icons = {
      people:'<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75"/>',
      search:'<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/>',
      back:'<path d="m15 18-6-6 6-6"/>',
      plus:'<path d="M12 5v14M5 12h14"/>',
      file:'<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/>',
      compare:'<path d="M7 3v14M3 7l4-4 4 4M17 21V7M13 17l4 4 4-4"/>',
      note:'<path d="M4 4h16v16H4z"/><path d="M8 9h8M8 13h8M8 17h5"/>'
    };
    return `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${icons[name]||icons.file}</svg>`;
  };

  const avatar = (key) => {
    const sets = {
      AR:['#E2EEF7','#336585','#F0B28A','#5C382B'],
      JK:['#F3E6DB','#865A45','#DFA47D','#34251F'],
      MT:['#EFE3F1','#75507B','#8A5A43','#2A201E'],
      RS:['#E9F1E8','#4E745C','#EDB786','#A45F3A'],
      TP:['#E9E5F5','#625B91','#C98E69','#3C2D28'],
      CL:['#F4EBD8','#886B37','#D9A27A','#5B4032']
    };
    const [bg,shirt,skin,hair] = sets[key] || sets.AR;
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 80 80"><rect width="80" height="80" rx="22" fill="${bg}"/><path d="M15 80c2-17 11-27 25-27s23 10 25 27" fill="${shirt}"/><circle cx="40" cy="33" r="18" fill="${skin}"/><path d="M22 31c0-16 8-23 19-23 13 0 19 9 19 22-6-2-9-7-11-11-5 7-14 10-27 12Z" fill="${hair}"/><circle cx="34" cy="34" r="1.7" fill="#352921"/><circle cx="47" cy="34" r="1.7" fill="#352921"/><path d="M35 43c3 2 7 2 10 0" fill="none" stroke="#8B5C4B" stroke-width="1.6" stroke-linecap="round"/></svg>`;
    return `data:image/svg+xml;charset=UTF-8,${encodeURIComponent(svg)}`;
  };

  const CLIENTS = [
    {id:'83F2A1',key:'AR',name:'Alex R.',level:'C5',activity:'Shooters',goal:'Improve aiming and turning',status:'Active',last:'Sep 1, 2026',device:'QuadStick',rig:'Wheelchair mount',inputs:'Sip and puff',notes:'Good head control. Motivated for gaming.',profiles:[
      {name:'Shooters v3',date:'Sep 1',current:true,settings:{right:68,left:58,sip:37,puff:42,deadzone:5,smoothing:1},note:'Reduced accidental activation considerably.'},
      {name:'Shooters v2',date:'Aug 14',settings:{right:54,left:58,sip:41,puff:42,deadzone:5,smoothing:1},note:'Better baseline, turning still slow.'},
      {name:'Default',date:'Jul 22',settings:{right:46,left:52,sip:44,puff:45,deadzone:7,smoothing:0},note:'Initial setup.'}],
      sessions:[
        {date:'Sep 1',kind:'Session',feedback:'Accuracy is much better, but turning still feels slow.',changes:['Right sensitivity 54 → 68','Sip threshold 41 → 37'],outcome:'Improved accuracy',tone:'good',profile:'Shooters v3'},
        {date:'Aug 14',kind:'Session',feedback:'Fine aiming is hard, especially when I need to turn quickly.',changes:['Created Shooters v2','Reduced deadzone 7 → 5'],outcome:'Better response',tone:'good',profile:'Shooters v2'},
        {date:'Jul 22',kind:'Evaluation',feedback:'First fitting and baseline mapping.',changes:['Baseline created','Mount position recorded'],outcome:'Baseline established',tone:'same',profile:'Default'}]},
    {id:'71AC42',key:'JK',name:'Jamie K.',level:'C5',activity:'Shooters',goal:'Fine aiming with less fatigue',status:'Active',last:'Aug 28, 2026',device:'QuadStick',rig:'Desk arm mount',inputs:'Sip and puff',notes:'Strong lip control. Long sessions cause fatigue.',profiles:[{name:'FPS Final',date:'Aug 28',current:true,settings:{right:76,left:61,sip:35,puff:40,deadzone:3,smoothing:1},note:'Much better control; turning still slightly slow.'},{name:'FPS v2',date:'Aug 2',settings:{right:62,left:59,sip:39,puff:42,deadzone:5,smoothing:1},note:'Early shooter setup.'}],sessions:[{date:'Aug 28',kind:'Session',feedback:'Much better control. I can hold aim longer now.',changes:['Right sensitivity 62 → 76','Sip threshold 39 → 35','Deadzone 5 → 3'],outcome:'Improved aiming',tone:'good',profile:'FPS Final'},{date:'Aug 2',kind:'Session',feedback:'Aiming feels heavy after about twenty minutes.',changes:['Created FPS v2','Custom acceleration curve'],outcome:'Partial improvement',tone:'mixed',profile:'FPS v2'}]},
    {id:'4D91B3',key:'MT',name:'Morgan T.',level:'C4',activity:'Gaming',goal:'Reliable movement',status:'Active',last:'Aug 20, 2026',device:'QuadStick',rig:'Chair mount',inputs:'Sip + switches',notes:'Uses external switch for mode changes.',profiles:[{name:'Gaming v4',date:'Aug 20',current:true,settings:{right:63,left:64,sip:38,puff:39,deadzone:4,smoothing:1},note:'Smoother movement, less accidental activation.'},{name:'Gaming v3',date:'Jul 30',settings:{right:57,left:60,sip:41,puff:41,deadzone:6,smoothing:0},note:'Stable baseline.'}],sessions:[{date:'Aug 20',kind:'Session',feedback:'Movement is smoother and I am hitting fewer accidental inputs.',changes:['Acceleration adjusted','Lip mapping changed','Deadzone 6 → 4'],outcome:'Better response',tone:'good',profile:'Gaming v4'}]},
    {id:'9E7C11',key:'RS',name:'Riley S.',level:'C5',activity:'Work',goal:'Reliable switch setup',status:'Active',last:'Aug 15, 2026',device:'QuadStick',rig:'Desk mount',inputs:'Sip + head switch',notes:'Primary use is productivity and communication.',profiles:[{name:'Work v2',date:'Aug 15',current:true,settings:{right:48,left:47,sip:36,puff:38,deadzone:6,smoothing:1},note:'Aiming improved but new switch issue appeared.'},{name:'Work v1',date:'Jul 18',settings:{right:45,left:45,sip:40,puff:40,deadzone:6,smoothing:0},note:'Initial work profile.'}],sessions:[{date:'Aug 15',kind:'Session',feedback:'Cursor is better, but the new switch sometimes fires twice.',changes:['Puff threshold changed','Alternative button mapping'],outcome:'Mixed results',tone:'mixed',profile:'Work v2'}]},
    {id:'2B4F90',key:'TP',name:'Taylor P.',level:'C6',activity:'Shooters',goal:'New shooter setup',status:'Active',last:'Aug 10, 2026',device:'QuadStick',rig:'Chair mount',inputs:'Sip and puff',notes:'Fast learner, prefers low resistance.',profiles:[{name:'Shooter v1',date:'Aug 10',current:true,settings:{right:72,left:65,sip:34,puff:38,deadzone:4,smoothing:1},note:'Good first shooter setup.'}],sessions:[{date:'Aug 10',kind:'Evaluation',feedback:'This already feels quicker than my old profile.',changes:['New shooter profile','Lower sip threshold'],outcome:'Promising start',tone:'good',profile:'Shooter v1'}]},
    {id:'7A2D18',key:'CL',name:'Casey L.',level:'C5',activity:'Evaluation',goal:'Find reliable inputs',status:'Active',last:'Jul 28, 2026',device:'QuadStick',rig:'Trial mount',inputs:'Lip + sip',notes:'Early evaluation. Still testing fatigue and reach.',profiles:[{name:'Trial v1',date:'Jul 28',current:true,settings:{right:50,left:50,sip:42,puff:44,deadzone:7,smoothing:0},note:'Evaluation baseline.'}],sessions:[{date:'Jul 28',kind:'Evaluation',feedback:'Sip feels reliable. Lip right is harder to repeat.',changes:['Trial profile created','Baseline thresholds recorded'],outcome:'Baseline established',tone:'same',profile:'Trial v1'}]}
  ];

  const state = {view:'caseload',clientId:'83F2A1',tab:'timeline',compare:null,search:'',outcome:'Better',focus:'Aiming'};
  const byId = id => CLIENTS.find(c => c.id === id) || CLIENTS[0];
  const current = () => byId(state.clientId);
  const profile = (c, name) => c.profiles.find(p => p.name === name) || c.profiles[0];
  const person = (c, size='') => `<div class="qcd-person"><img class="qcd-avatar ${size}" src="${avatar(c.key)}" alt=""><span><b>${c.name}</b><small>${c.level} · ${c.activity}</small></span></div>`;
  const outcome = (text,tone='good') => `<span class="qcd-outcome ${tone}">${text}</span>`;

  mount.innerHTML = `<div class="screen"><div class="qcd-app">
    <aside class="qcd-side">
      <button class="qcd-brand" data-go="caseload" aria-label="Back to caseload"><img src="QSLogo.png" alt="">QuadStick Clinic</button>
      <nav class="qcd-nav">
        <button data-go="caseload">${icon('people')}Caseload</button>
        <button data-go="search">${icon('search')}Case Search</button>
      </nav>
      <div class="qcd-side-foot"><div class="qcd-clinician"><span class="qcd-doc-avatar">DS</span><span><b>Dr. Smith</b><span>Riverside Rehab</span></span></div></div>
    </aside>
    <main class="qcd-main">
      <div class="qcd-top">
        <button class="qcd-back" data-back>${icon('back')}<span>Caseload</span></button>
        <div class="qcd-search">${icon('search')}<input id="qcd-global-search" placeholder="Search clients, notes, profiles, goals…" aria-label="Search"></div>
        <div class="qcd-current" id="qcd-current"></div>
      </div>
      <div class="qcd-notice" id="qcd-notice" aria-live="polite"></div>
      <div class="qcd-content" id="qcd-content"></div>
    </main>
  </div></div>`;

  const content = mount.querySelector('#qcd-content');
  const noticeEl = mount.querySelector('#qcd-notice');
  const backEl = mount.querySelector('[data-back]');
  const currentEl = mount.querySelector('#qcd-current');
  let noticeTimer;
  function notice(message){
    clearTimeout(noticeTimer); noticeEl.textContent = message; noticeEl.classList.add('show');
    noticeTimer = setTimeout(() => noticeEl.classList.remove('show'), 3200);
  }

  function navState(){
    const isHome = state.view === 'caseload';
    backEl.classList.toggle('visible', !isHome);
    backEl.querySelector('span').textContent = state.view === 'client' || state.view === 'session' || state.view === 'compare' ? 'Caseload' : 'Caseload';
    currentEl.innerHTML = isHome ? '' : `<span>Working with</span><strong>${current().name} · ${current().id.slice(0,6)}</strong>`;
    mount.querySelectorAll('.qcd-nav button').forEach(b => b.classList.toggle('active', b.dataset.go === state.view || (b.dataset.go==='caseload' && state.view==='client')));
  }

  function renderCaseload(){
    state.view='caseload'; navState();
    const q = state.search.trim().toLowerCase();
    const rows = CLIENTS.filter(c => !q || [c.name,c.level,c.activity,c.goal,c.notes,...c.profiles.map(p=>p.name)].join(' ').toLowerCase().includes(q));
    content.innerHTML = `<div class="qcd-page-head"><div><h3>Caseload</h3><p>Pick up where the last visit ended.</p></div><div class="qcd-actions"><button class="qcd-btn primary" data-demo-only="Client creation">${icon('plus')} Add client</button></div></div>
      <div class="qcd-home-grid">
        <section class="qcd-panel">
          <div class="qcd-toolbar"><input id="qcd-roster-search" placeholder="Search this caseload…" value="${state.search}"><select><option>Active clients</option><option>Archived</option></select><select><option>All goals</option><option>Shooters</option><option>Daily use</option></select></div>
          <table class="qcd-table"><thead><tr><th>Client</th><th>Primary goal</th><th>Last activity</th><th>Status</th></tr></thead><tbody>
            ${rows.map(c=>`<tr data-client="${c.id}"><td>${person(c)}</td><td>${c.goal}</td><td>${c.last}</td><td><span class="qcd-status">${c.status}</span></td></tr>`).join('') || `<tr><td colspan="4"><div class="qcd-empty">No clients match that search.</div></td></tr>`}
          </tbody></table>
        </section>
        <aside class="qcd-panel qcd-recent"><h4>Recent work</h4>${CLIENTS.slice(0,4).map(c=>`<div class="qcd-recent-item"><img class="qcd-avatar" src="${avatar(c.key)}" alt=""><div class="qcd-recent-copy"><b>${c.name}</b><span>${c.sessions[0].kind}: ${c.sessions[0].profile}</span></div><time>${c.sessions[0].date}</time></div>`).join('')}</aside>
      </div>`;
    const rosterSearch = content.querySelector('#qcd-roster-search');
    rosterSearch?.addEventListener('input', e => { state.search=e.target.value; renderCaseload(); const n=content.querySelector('#qcd-roster-search'); n?.focus(); n?.setSelectionRange(n.value.length,n.value.length); });
  }

  function similarFor(c){
    return CLIENTS.filter(x=>x.id!==c.id).map(x=>{
      let score=42; const why=[];
      if(x.level===c.level){score+=18;why.push(`Same level (${c.level})`)}
      if(x.activity===c.activity){score+=17;why.push(`Same activity (${c.activity})`)}
      if(x.device===c.device){score+=8;why.push('Same device')}
      if(x.inputs===c.inputs){score+=8;why.push('Similar input method')}
      if(x.goal.toLowerCase().includes('aim') && c.goal.toLowerCase().includes('aim')){score+=7;why.push('Similar aiming goal')}
      return {client:x,score:Math.min(score,94),why};
    }).sort((a,b)=>b.score-a.score);
  }

  function renderClient(){
    state.view='client'; navState(); const c=current(); const sims=similarFor(c).slice(0,2);
    const tabBody = state.tab==='timeline' ? renderTimeline(c,sims) : state.tab==='profiles' ? renderProfiles(c) : state.tab==='notes' ? renderNotes(c) : renderDetails(c);
    content.innerHTML = `<div class="qcd-client-hero">
      <div class="qcd-client-left"><img class="qcd-avatar xl" src="${avatar(c.key)}" alt=""><div class="qcd-client-copy"><h3>${c.name} <span class="qcd-id">${c.id}</span></h3><div class="meta"><span>${c.level} · ${c.activity}</span><span class="qcd-status">${c.status}</span><span>${c.device}</span></div><div class="goal"><strong>Primary goal:</strong> ${c.goal}</div></div></div>
      <div class="qcd-mini-stats"><div><b>${c.last}</b><span>Last activity</span></div><div><b>${c.profiles.length}</b><span>Profile versions</span></div><div class="qcd-actions"><button class="qcd-btn primary" data-session>Log session</button></div></div>
    </div>
    <div class="qcd-tabs">${[['timeline','Timeline'],['profiles','Profiles'],['notes','Notes'],['details','Details']].map(([k,l])=>`<button data-tab="${k}" class="${state.tab===k?'active':''}">${l}</button>`).join('')}</div>${tabBody}`;
  }

  function renderTimeline(c,sims){
    return `<div class="qcd-workspace"><section class="qcd-panel" style="padding:11px 12px"><div class="qcd-timeline">${c.sessions.map((s,i)=>`<article class="qcd-event"><time class="qcd-event-date">${s.date}</time><i class="qcd-event-dot"></i><div class="qcd-session-card"><div class="qcd-session-top"><div class="qcd-session-title"><b>${s.kind}</b><span>${s.profile}</span></div><span class="qcd-session-time">${i===0?'Latest':''}</span></div><div class="qcd-quote">“${s.feedback}”</div><div class="qcd-change-list">${s.changes.map(ch=>`<span class="qcd-change">${ch}</span>`).join('')}</div><div class="qcd-session-foot">${outcome(s.outcome,s.tone)}<div class="qcd-session-actions"><button class="qcd-btn" data-snapshot>Open snapshot</button>${i<c.sessions.length-1?`<button class="qcd-btn soft" data-compare-previous="${i}">${icon('compare')} Compare previous</button>`:''}</div></div></div></article>`).join('')}</div></section>
      <aside class="qcd-similar"><div class="qcd-similar-head"><b>Similar cases</b><button class="qcd-link" data-find-similar>Find more →</button></div>${sims.map(s=>`<div class="qcd-case-card"><div class="qcd-case-top"><div class="qcd-case-person"><img class="qcd-avatar" src="${avatar(s.client.key)}" alt=""><span><b>${s.client.name}</b><span>${s.client.level} · ${s.client.activity}</span></span></div><span class="qcd-match">${s.score}% match</span></div><div class="qcd-why"><b>Why this matches</b><ul>${s.why.slice(0,3).map(w=>`<li>${w}</li>`).join('')}</ul></div><div class="qcd-case-outcome">${outcome(s.client.sessions[0].outcome,s.client.sessions[0].tone)}</div><div class="qcd-case-actions"><button class="qcd-btn" data-client="${s.client.id}">View case</button><button class="qcd-btn soft" data-compare-client="${s.client.id}">Compare</button></div></div>`).join('')}</aside></div>`;
  }

  function renderProfiles(c){
    return `<div class="qcd-profile-list">${c.profiles.map((p,i)=>`<div class="qcd-profile-row"><span class="qcd-profile-icon">▦</span><span><b>${p.name}${p.current?' · Current':''}</b><span>${p.date} · ${p.note}</span></span><div class="qcd-actions">${i<c.profiles.length-1?`<button class="qcd-btn soft" data-profile-compare="${i}">Compare previous</button>`:''}<button class="qcd-btn" data-snapshot>Open</button></div></div>`).join('')}</div>`;
  }
  function renderNotes(c){return `<div class="qcd-detail-grid">${c.sessions.map(s=>`<div class="qcd-detail-card"><b>${s.date} · ${s.kind}</b><strong>${s.profile}</strong><p>${s.feedback}</p></div>`).join('')}<div class="qcd-detail-card"><b>Standing note</b><strong>Clinical context</strong><p>${c.notes}</p></div></div>`}
  function renderDetails(c){return `<div class="qcd-detail-grid"><div class="qcd-detail-card"><b>Level</b><strong>${c.level}</strong></div><div class="qcd-detail-card"><b>Primary activity</b><strong>${c.activity}</strong></div><div class="qcd-detail-card"><b>Device</b><strong>${c.device}</strong></div><div class="qcd-detail-card"><b>Mounting</b><strong>${c.rig}</strong></div><div class="qcd-detail-card"><b>Primary inputs</b><strong>${c.inputs}</strong></div><div class="qcd-detail-card"><b>Goal</b><strong>${c.goal}</strong><p>${c.notes}</p></div></div>`}

  function renderSearch(){
    state.view='search'; navState(); const anchor=current(); const results=similarFor(anchor);
    content.innerHTML=`<div class="qcd-page-head"><div><h3>Case Search</h3><p>Find what worked for similar clients in your own caseload.</p></div></div><div class="qcd-search-page"><div class="qcd-case-searchbar"><input value="${anchor.level} ${anchor.activity.toLowerCase()}" aria-label="Case search"><button class="qcd-btn primary">Search</button><button class="qcd-btn soft" data-find-like>Find cases like ${anchor.name.replace('.','')}</button></div><div class="qcd-filter-row"><button class="qcd-filter">Level: ${anchor.level}</button><button class="qcd-filter">Goal: ${anchor.activity}</button><button class="qcd-filter">Device: ${anchor.device}</button><button class="qcd-filter">All outcomes</button></div>${results.map(r=>`<article class="qcd-result"><div class="qcd-result-head"><div>${person(r.client)}</div><span class="qcd-match">${r.score}% match</span></div><div class="qcd-result-grid"><div><h5>Why it matches</h5><ul>${r.why.map(w=>`<li>${w}</li>`).join('')}</ul></div><div><h5>What they tried</h5><ul>${r.client.sessions[0].changes.map(ch=>`<li>${ch}</li>`).join('')}</ul></div><div><h5>Outcome</h5>${outcome(r.client.sessions[0].outcome,r.client.sessions[0].tone)}<p style="margin-top:5px">“${r.client.sessions[0].feedback}”</p></div></div><div class="qcd-result-actions"><button class="qcd-btn" data-client="${r.client.id}">View case</button><button class="qcd-btn soft" data-compare-client="${r.client.id}">Compare</button></div></article>`).join('')}</div>`;
  }

  function startCompare(leftClient,rightClient,leftProfile,rightProfile,returnView='client'){
    state.compare={leftClient:leftClient.id,rightClient:rightClient.id,leftProfile:leftProfile.name,rightProfile:rightProfile.name,returnView}; state.view='compare'; renderCompare();
  }
  function renderCompare(){
    navState(); const cmp=state.compare; if(!cmp){renderClient();return} const a=byId(cmp.leftClient),b=byId(cmp.rightClient),pa=profile(a,cmp.leftProfile),pb=profile(b,cmp.rightProfile); const settings=[['right','Right stick sensitivity','Higher = faster turning'],['left','Left stick sensitivity','Higher = faster movement'],['sip','Sip threshold','Lower = easier activation'],['puff','Puff threshold','Lower = easier activation'],['deadzone','Deadzone','Lower = more responsive'],['smoothing','Smoothing','0 off · 1 on']];
    const sameClient=a.id===b.id;
    content.innerHTML=`<div class="qcd-compare-head"><div class="qcd-compare-title"><h3>${sameClient?'Profile history':'Case comparison'}</h3><p>${sameClient?'See exactly what changed between visits.':'Compare context and settings without leaving the current case.'}</p></div><button class="qcd-btn" data-return-client>${icon('back')} Back to ${current().name}</button></div><div class="qcd-compare-people"><div class="qcd-compare-person"><img class="qcd-avatar lg" src="${avatar(a.key)}" alt=""><span><b>${a.name} · ${pa.name}</b><span>${a.level} · ${a.goal}</span></span></div><span class="qcd-vs">VS</span><div class="qcd-compare-person"><img class="qcd-avatar lg" src="${avatar(b.key)}" alt=""><span><b>${b.name} · ${pb.name}</b><span>${b.level} · ${b.goal}</span></span></div></div><div class="qcd-diff-table"><div class="qcd-diff-row header"><div>Setting</div><div>${pa.name}</div><div>${pb.name}</div><div>Difference</div></div>${settings.map(([k,label,desc])=>{const av=pa.settings[k],bv=pb.settings[k],d=bv-av;const cls=d>0?'up':d<0?'down':'same';const max=k==='smoothing'?1:100;return `<div class="qcd-diff-row"><div class="qcd-diff-label"><b>${label}</b><span>${desc}</span></div><div class="qcd-value">${av}<div class="qcd-bars"><span class="qcd-bar"><i style="width:${Math.min(100,av/max*100)}%"></i></span></div></div><div class="qcd-value">${bv}<div class="qcd-bars"><span class="qcd-bar"><i style="width:${Math.min(100,bv/max*100)}%"></i></span></div></div><div class="qcd-delta ${cls}">${d===0?'Same':`${d>0?'+':''}${d}`}</div></div>`}).join('')}</div><div class="qcd-compare-note"><div class="qcd-note-card"><b>${pa.name} · clinical note</b><p>${pa.note}</p></div><div class="qcd-note-card"><b>${pb.name} · clinical note</b><p>${pb.note}</p></div></div>`;
  }

  function renderSession(){
    state.view='session'; navState(); const c=current(),p=c.profiles[0],prev=c.profiles[1]||p;
    content.innerHTML=`<div class="qcd-page-head"><div><h3>New session · ${c.name}</h3><p>Capture the context, what changed, and whether it helped.</p></div></div><div class="qcd-form-grid"><form class="qcd-session-form" id="qcd-session-form"><div class="qcd-field"><label>Focus today</label><div class="qcd-focuses">${['Aiming','Turning','Navigation','Setup','Other'].map(f=>`<button type="button" class="qcd-focus ${state.focus===f?'active':''}" data-focus="${f}">${f}</button>`).join('')}</div></div><div class="qcd-field"><label>Client feedback</label><textarea id="qcd-feedback" placeholder="What felt better, worse, or different?">Turning feels better. I still want a little more speed.</textarea></div><div class="qcd-field"><label>What changed?</label><div class="qcd-change-grid"><span>Right stick sensitivity</span><input id="qcd-right-old" value="${p.settings.right}"><span class="qcd-arrow">→</span><input id="qcd-right-new" value="${Math.min(100,p.settings.right+4)}"></div><div class="qcd-change-grid"><span>Sip threshold</span><input id="qcd-sip-old" value="${p.settings.sip}"><span class="qcd-arrow">→</span><input id="qcd-sip-new" value="${Math.max(0,p.settings.sip-2)}"></div></div><div class="qcd-field"><label>Clinician note</label><textarea id="qcd-note" placeholder="Optional note">Reduced accidental activation; continue testing turning speed.</textarea></div><div class="qcd-actions"><button type="submit" class="qcd-btn primary">Save session</button><button type="button" class="qcd-btn" data-return-client>Cancel</button></div></form><aside class="qcd-outcome-panel"><h4>Outcome</h4>${['Better','Same','Worse','Not assessed'].map(o=>`<button type="button" class="qcd-outcome-option ${state.outcome===o?'active':''}" data-outcome="${o}"><span class="qcd-dot"></span>${o}</button>`).join('')}<div class="qcd-detail-card" style="margin-top:10px"><b>Current profile</b><strong>${p.name}</strong><p>Previous: ${prev.name}</p></div></aside></div>`;
    content.querySelector('#qcd-session-form')?.addEventListener('submit',e=>{e.preventDefault();const rightOld=content.querySelector('#qcd-right-old').value,rightNew=content.querySelector('#qcd-right-new').value,sipOld=content.querySelector('#qcd-sip-old').value,sipNew=content.querySelector('#qcd-sip-new').value,feedback=content.querySelector('#qcd-feedback').value.trim()||'Session feedback recorded.',note=content.querySelector('#qcd-note').value.trim(); c.sessions.unshift({date:'Today',kind:'Session',feedback,changes:[`Right sensitivity ${rightOld} → ${rightNew}`,`Sip threshold ${sipOld} → ${sipNew}`],outcome:state.outcome==='Better'?'Improved response':state.outcome,tone:state.outcome==='Better'?'good':state.outcome==='Same'?'same':'mixed',profile:p.name}); p.settings.right=Number(rightNew)||p.settings.right;p.settings.sip=Number(sipNew)||p.settings.sip;if(note)p.note=note;c.last='Today';state.tab='timeline';renderClient();notice('Session added to this client’s timeline.');});
  }

  function goClient(id){state.clientId=id;state.tab='timeline';renderClient();}
  mount.addEventListener('click',e=>{
    const btn=e.target.closest('button,[data-client]'); if(!btn)return;
    if(btn.dataset.go==='caseload'){state.search='';renderCaseload();return}
    if(btn.dataset.go==='search'){renderSearch();return}
    if(btn.hasAttribute('data-back')){renderCaseload();return}
    if(btn.dataset.client){goClient(btn.dataset.client);return}
    if(btn.dataset.tab){state.tab=btn.dataset.tab;renderClient();return}
    if(btn.hasAttribute('data-session')){renderSession();return}
    if(btn.hasAttribute('data-find-similar')||btn.hasAttribute('data-find-like')){renderSearch();return}
    if(btn.dataset.compareClient){const other=byId(btn.dataset.compareClient);startCompare(current(),other,current().profiles[0],other.profiles[0]);return}
    if(btn.dataset.comparePrevious!==undefined){const c=current(),i=Number(btn.dataset.comparePrevious),a=c.profiles[Math.min(i,c.profiles.length-1)]||c.profiles[0],b=c.profiles[Math.min(i+1,c.profiles.length-1)]||c.profiles[1]||a;startCompare(c,c,a,b);return}
    if(btn.dataset.profileCompare!==undefined){const c=current(),i=Number(btn.dataset.profileCompare),a=c.profiles[i],b=c.profiles[i+1]||a;startCompare(c,c,a,b);return}
    if(btn.hasAttribute('data-return-client')){renderClient();return}
    if(btn.hasAttribute('data-snapshot')){notice('Profile files are not opened from the website demo. In Clinic, this opens the saved snapshot.');return}
    if(btn.dataset.demoOnly){notice(`${btn.dataset.demoOnly} is intentionally disabled in the website demo.`);return}
    if(btn.dataset.focus){state.focus=btn.dataset.focus;renderSession();return}
    if(btn.dataset.outcome){state.outcome=btn.dataset.outcome;renderSession();return}
  });

  mount.querySelector('#qcd-global-search')?.addEventListener('input',e=>{state.search=e.target.value;if(state.view!=='caseload')renderCaseload();else renderCaseload();const n=mount.querySelector('#qcd-global-search');if(n){n.value=state.search;n.focus();n.setSelectionRange(n.value.length,n.value.length)}});
  renderCaseload();
})();