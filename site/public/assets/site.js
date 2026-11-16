(function(){
  var b=document.querySelectorAll('.filters button'),c=document.querySelectorAll('[data-layer]');
  b.forEach(function(x){x.addEventListener('click',function(){
    b.forEach(function(y){y.setAttribute('aria-pressed',y===x?'true':'false')});
    var f=x.dataset.f;c.forEach(function(e){e.hidden=!(f==='all'||e.dataset.layer===f||e.dataset.stab===f)});
  })});
})();
