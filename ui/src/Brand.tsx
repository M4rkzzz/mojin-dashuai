export function Logo({className=''}:{className?:string}) {
 return <img className={`brand-logo ${className}`} src="./brand/logo.png" alt="" aria-hidden="true" draggable={false}/>;
}

export function Brand({small=false}:{small?:boolean}) {
 return <div className={`brand ${small?'small':''}`}><Logo/>魔金大帅</div>;
}
