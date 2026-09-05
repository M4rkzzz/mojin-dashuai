export function Logo({className=''}:{className?:string}) {
 return <img className={`brand-logo ${className}`} src="./brand/logo.png" alt="" aria-hidden="true" draggable={false}/>;
}

export function Brand({small=false}:{small?:boolean}) {
 return <div className={`brand ${small?'small':''}`}><Logo/>魔金大帅</div>;
}

export function ServerIcon({id,className=''}:{id:string;className?:string}) {
 return <img className={`brand-logo server-logo ${className}`} src={`./servers/${id}.png`} alt="" aria-hidden="true" draggable={false}/>;
}
